using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Owns a project's Business Central SaaS connection: the encrypted S2S secret, the
/// "Test connection" / "Refresh environments" round-trips (token + list environments,
/// flagging missing GDAP), and per-environment company discovery. Access-gated to the
/// project owner / org Admin via <see cref="ProjectAccess"/>; org-scoped through the EF
/// query filter. The secret is encrypted with the Data Protection key ring under
/// <see cref="SecretProtectionPurpose"/> (the SMTP-password / repository-token
/// precedent), written only here, and never returned to callers. See
/// <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ProjectConnectionService : IDeliveryTokenSource
{
    /// <summary>Data Protection purpose string for a project's BC S2S client secret.</summary>
    public const string SecretProtectionPurpose = "ALDevToolbox.ProjectBcSecret";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly BcTokenService _tokens;
    private readonly IBcAdminClient _adminClient;
    private readonly IBcAppManagementClient _apps;
    private readonly IDataProtector _secretProtector;
    private readonly BcPanelCache _panelCache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectConnectionService> _logger;

    public ProjectConnectionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        BcTokenService tokens,
        IBcAdminClient adminClient,
        IBcAppManagementClient apps,
        IDataProtectionProvider protectionProvider,
        BcPanelCache panelCache,
        TimeProvider clock,
        ILogger<ProjectConnectionService> logger,
        Notifications.EnvironmentUpdateNotifier? updateNotifier = null)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _tokens = tokens;
        _adminClient = adminClient;
        _apps = apps;
        _secretProtector = protectionProvider.CreateProtector(SecretProtectionPurpose);
        _panelCache = panelCache;
        _clock = clock;
        _logger = logger;
        _updateNotifier = updateNotifier;
    }

    /// <summary>
    /// Tells a solution's followers when a refresh finds its next Business Central update
    /// scheduled, moved, or close to the latest date it can be postponed to (#1049).
    /// Optional so the tests that build this service by hand need not wire notifications.
    /// </summary>
    private readonly Notifications.EnvironmentUpdateNotifier? _updateNotifier;

    /// <summary>Changes found since the last save, announced once the rows are written.</summary>
    private readonly List<BcUpdateScheduleChange> _scheduleChanges = new();

    /// <summary>
    /// Writes the picked update onto a row read from Business Central, remembering what it
    /// changed. Only the refresh paths use this: a change the app made itself was already
    /// written by the write path, so the next read finds nothing new to announce.
    /// </summary>
    private void ApplyReadNextUpdate(OeProjectEnvironment row, IReadOnlyList<BcEnvironmentUpdate> updates)
    {
        var before = BcUpdateScheduleSnapshot.Of(row);
        ApplyNextUpdate(row, updates);
        if (before.CompareWith(row, _clock.GetUtcNow().UtcDateTime) is { } change) _scheduleChanges.Add(change);
    }

    /// <summary>
    /// Announces the changes found by the reads just saved. Not cancellable: the rows
    /// already hold the new values, so a cancel here would lose the news for good. The
    /// notifier never throws.
    /// </summary>
    private async Task AnnounceScheduleChangesAsync()
    {
        if (_scheduleChanges.Count == 0) return;
        var changes = _scheduleChanges.ToList();
        _scheduleChanges.Clear();
        if (_updateNotifier is not null) await _updateNotifier.NotifyAsync(changes, CancellationToken.None);
    }

    /// <summary>
    /// The live projects whose connection is complete enough to answer: a tenant id and
    /// a registration with a secret - their own, or none of their own and the
    /// organisation's to fall back on. The same test as
    /// <see cref="BcConnectionStatus.IsConfigured"/> from <see cref="GetConnectionAsync"/>,
    /// written as one translatable query so a list or a sweep can ask it of every
    /// project at once. It looks at whether a secret is stored, never at the secret.
    /// Org-scoped by the EF query filter on <paramref name="db"/>; both halves are the
    /// solution's own organisation.
    /// </summary>
    public static IQueryable<OeProject> ConfiguredProjects(AppDbContext db) =>
        db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null
                && p.BcTenantId != null
                && (p.BcClientId != null
                    ? p.BcClientSecretEncrypted != null
                    : db.OrganizationSettings.Any(o => o.OrganizationId == p.OrganizationId
                        && o.BcClientId != null && o.BcClientSecretEncrypted != null)));

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; BC connection mutation called outside an authenticated request.");

    /// <summary>Presence/verification view of a project's BC connection — never the secret. Null when the project doesn't exist in this org.</summary>
    public async Task<BcConnectionStatus?> GetConnectionAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var p = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new
            {
                c.BcTenantId,
                c.BcClientId,
                HasSecret = c.BcClientSecretEncrypted != null,
                c.BcClientSecretExpiresAt,
                c.BcCredentialsUpdatedAt,
                c.BcTimeZone,
                c.BcConnectionVerifiedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var orgId = RequireOrganizationId();
        var shared = await _db.OrganizationSettings.AsNoTracking()
            .Where(o => o.OrganizationId == orgId)
            .Select(o => new { o.BcClientId, HasSecret = o.BcClientSecretEncrypted != null, o.BcClientSecretExpiresAt })
            .FirstOrDefaultAsync(ct);
        var sharedAvailable = !string.IsNullOrEmpty(shared?.BcClientId) && shared.HasSecret;

        var usesShared = string.IsNullOrEmpty(p.BcClientId);
        var configured = p.BcTenantId is not null
            && (usesShared ? sharedAvailable : p.HasSecret);
        return new BcConnectionStatus(
            configured, p.BcTenantId, p.BcClientId, p.HasSecret,
            p.BcClientSecretExpiresAt, p.BcCredentialsUpdatedAt, p.BcTimeZone, p.BcConnectionVerifiedAt,
            UsesOrganizationRegistration: usesShared && sharedAvailable,
            OrganizationRegistrationAvailable: sharedAvailable,
            EffectiveSecretExpiresAt: usesShared ? (sharedAvailable ? shared!.BcClientSecretExpiresAt : null) : p.BcClientSecretExpiresAt,
            // Not a secret, and the person wiring up a customer needs it: it is what the
            // customer types into their admin centre to authorise the connection.
            OrganizationClientId: sharedAvailable ? shared!.BcClientId : null);
    }

    // ── The organisation's own app registration ───────────────────────────────

    /// <summary>
    /// The organisation's default app registration, as the Administration page shows it -
    /// never the secret. Admin-only, like the write.
    /// </summary>
    public async Task<OrganizationBcRegistration> GetOrganizationRegistrationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);
        var row = await _db.OrganizationSettings.AsNoTracking()
            .Where(o => o.OrganizationId == orgId)
            .Select(o => new
            {
                o.BcClientId,
                HasSecret = o.BcClientSecretEncrypted != null,
                o.BcClientSecretExpiresAt,
                o.DefaultDeliveryWindowProductionStart,
                o.DefaultDeliveryWindowProductionEnd,
                o.DefaultDeliveryWindowSandboxStart,
                o.DefaultDeliveryWindowSandboxEnd,
            })
            .FirstOrDefaultAsync(ct);
        var (usingIt, withTheirOwn) = await CountSolutionsByRegistrationAsync(ct);
        var windows = row is null
            ? DefaultDeliveryWindows.None
            : new DefaultDeliveryWindows(
                row.DefaultDeliveryWindowProductionStart, row.DefaultDeliveryWindowProductionEnd,
                row.DefaultDeliveryWindowSandboxStart, row.DefaultDeliveryWindowSandboxEnd);
        return new OrganizationBcRegistration(
            row?.BcClientId, row?.HasSecret ?? false, row?.BcClientSecretExpiresAt, usingIt, withTheirOwn, windows);
    }

    /// <summary>
    /// Saves the organisation's default app registration. The secret is keep-on-blank,
    /// as on a solution. Every solution connecting through it re-authenticates on its
    /// next call, and has to be tested again: the last successful test was of the old
    /// credentials.
    /// </summary>
    public async Task SaveOrganizationRegistrationAsync(OrganizationBcRegistrationInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }

        var errors = new Dictionary<string, string>();
        var clientId = (input.ClientId ?? string.Empty).Trim();
        if (!Guid.TryParse(clientId, out _))
        {
            errors["BcClientId"] = "Enter the app registration's Application (client) ID - a GUID like 00000000-0000-0000-0000-000000000000.";
        }
        var newSecret = input.ClientSecret?.Trim();
        var settingSecret = !string.IsNullOrEmpty(newSecret);
        if (!settingSecret && row.BcClientSecretEncrypted is null)
        {
            errors["BcClientSecret"] = "Enter the app registration's client secret.";
        }
        if (settingSecret && input.SecretExpiresAt is null)
        {
            errors["BcClientSecretExpiresAt"] = "Enter when the secret expires (Entra shows this when you create it).";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        row.BcClientId = clientId.ToLowerInvariant();
        if (settingSecret)
        {
            row.BcClientSecretEncrypted = _secretProtector.Protect(newSecret!);
            row.BcClientSecretExpiresAt = DateTime.SpecifyKind(input.SecretExpiresAt!.Value, DateTimeKind.Utc);
        }
        row.UpdatedAt = DateTime.UtcNow;

        var affected = await ResetSolutionsOnOrganizationRegistrationAsync(ct);
        await _db.SaveChangesAsync(ct);
        foreach (var projectId in affected) _tokens.Invalidate(projectId);

        _logger.LogInformation(
            "User {UserId} saved org {OrgId}'s Business Central app registration (secretChanged={SecretChanged}); {Count} solutions connect through it.",
            _orgContext.CurrentUserId, orgId, settingSecret, affected.Count);
    }

    /// <summary>
    /// Removes the organisation's default app registration. Solutions that were
    /// connecting through it stop connecting until they are given a registration of
    /// their own or a new default is saved - which is why the page says how many first.
    /// </summary>
    public async Task ClearOrganizationRegistrationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == orgId, ct);
        if (row is null || row.BcClientId is null) return;

        row.BcClientId = null;
        row.BcClientSecretEncrypted = null;
        row.BcClientSecretExpiresAt = null;
        row.UpdatedAt = DateTime.UtcNow;

        var affected = await ResetSolutionsOnOrganizationRegistrationAsync(ct);
        await _db.SaveChangesAsync(ct);
        foreach (var projectId in affected) _tokens.Invalidate(projectId);

        _logger.LogInformation(
            "User {UserId} removed org {OrgId}'s Business Central app registration; {Count} solutions were connecting through it.",
            _orgContext.CurrentUserId, orgId, affected.Count);
    }

    /// <summary>Clears "verified" on every solution with no registration of its own, and returns their ids. The caller saves.</summary>
    private async Task<List<int>> ResetSolutionsOnOrganizationRegistrationAsync(CancellationToken ct)
    {
        var solutions = await _db.OeProjects
            .Where(p => p.DeletedAt == null && p.BcTenantId != null && p.BcClientId == null)
            .ToListAsync(ct);
        foreach (var solution in solutions) solution.BcConnectionVerifiedAt = null;
        return solutions.Select(p => p.Id).ToList();
    }

    private async Task<(int UsingIt, int WithTheirOwn)> CountSolutionsByRegistrationAsync(CancellationToken ct)
    {
        var connected = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.BcTenantId != null)
            .Select(p => p.BcClientId != null)
            .ToListAsync(ct);
        return (connected.Count(own => !own), connected.Count(own => own));
    }

    private async Task EnsureOrganizationAdminAsync(CancellationToken ct)
    {
        if (!await _access.IsOrganizationAdminAsync(ct))
        {
            throw new ProjectAccessDeniedException("Only an administrator can change your organisation's Business Central app registration.");
        }
    }

    /// <summary>
    /// Saves a project's BC connection. The secret follows keep-on-blank semantics: a
    /// non-empty value is encrypted and stored, an empty value leaves the stored secret
    /// untouched. Validates the tenant/client/secret/expiry/timezone and stamps
    /// <c>BcCredentialsUpdatedAt</c>; invalidates the cached token so the next call
    /// re-authenticates. Access-gated.
    /// </summary>
    public async Task SaveConnectionAsync(int projectId, BcConnectionInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        var errors = new Dictionary<string, string>();

        if (input.TenantId is null || input.TenantId == Guid.Empty)
        {
            errors["BcTenantId"] = "Enter the customer's Microsoft Entra tenant ID (a GUID).";
        }
        var clientId = (input.ClientId ?? string.Empty).Trim();
        var newSecret = input.ClientSecret?.Trim();
        var settingSecret = !input.UseOrganizationRegistration && !string.IsNullOrEmpty(newSecret);
        if (input.UseOrganizationRegistration)
        {
            var orgId = project.OrganizationId;
            var sharedAvailable = await _db.OrganizationSettings.AsNoTracking()
                .AnyAsync(o => o.OrganizationId == orgId && o.BcClientId != null && o.BcClientSecretEncrypted != null, ct);
            if (!sharedAvailable)
            {
                errors["BcClientId"] = "Your organisation has no app registration of its own yet. An administrator can add one under Administration, or enter this customer's here.";
            }
        }
        else
        {
            if (clientId.Length == 0)
            {
                errors["BcClientId"] = "Enter the app registration's client ID.";
            }
            var hasExistingSecret = project.BcClientSecretEncrypted is not null;
            if (!settingSecret && !hasExistingSecret)
            {
                errors["BcClientSecret"] = "Enter the app registration's client secret.";
            }
            if (settingSecret && input.SecretExpiresAt is null)
            {
                errors["BcClientSecretExpiresAt"] = "Enter when the secret expires (Entra shows this when you create it).";
            }
        }

        string? timeZone = null;
        if (!string.IsNullOrWhiteSpace(input.TimeZone))
        {
            timeZone = input.TimeZone.Trim();
            if (!IsValidTimeZone(timeZone))
            {
                errors["BcTimeZone"] = "Use an IANA time zone like 'Europe/Copenhagen'.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.BcTenantId = input.TenantId;
        project.BcTimeZone = timeZone;
        if (input.UseOrganizationRegistration)
        {
            // The solution's own registration goes with the choice: a client id left
            // behind is what says "this customer has their own", and a stored secret
            // nobody uses is one more thing to leak.
            project.BcClientId = null;
            project.BcClientSecretEncrypted = null;
            project.BcClientSecretExpiresAt = null;
        }
        else
        {
            project.BcClientId = clientId;
        }
        if (settingSecret)
        {
            project.BcClientSecretEncrypted = _secretProtector.Protect(newSecret!);
            project.BcClientSecretExpiresAt = DateTime.SpecifyKind(input.SecretExpiresAt!.Value, DateTimeKind.Utc);
        }
        project.BcCredentialsUpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        // Re-verification is required after a credential change; the previous verify no
        // longer reflects the live creds.
        project.BcConnectionVerifiedAt = null;

        await _db.SaveChangesAsync(ct);
        _tokens.Invalidate(projectId);
        _logger.LogInformation(
            "Saved BC connection for project {ProjectId} (secretChanged={SecretChanged}, organisation's registration={UsesOrganization}).",
            projectId, settingSecret, input.UseOrganizationRegistration);
    }

    /// <summary>
    /// Runs a "Test connection": acquires a token with the stored credentials and lists
    /// the customer's environments, persisting them (stable upsert) and stamping
    /// <c>BcConnectionVerifiedAt</c> on success. Classifies failures so the UI can render
    /// the GDAP-missing case clearly. Access-gated.
    /// </summary>
    public Task<BcConnectionTestResult> TestConnectionAsync(int projectId, CancellationToken ct = default)
        => FetchAndUpsertEnvironmentsAsync(projectId, markVerified: true, ct);

    /// <summary>Re-fetches and upserts the environment list using the stored credentials. Same round-trip as Test connection. Access-gated.</summary>
    public Task<BcConnectionTestResult> RefreshEnvironmentsAsync(int projectId, CancellationToken ct = default)
        => FetchAndUpsertEnvironmentsAsync(projectId, markVerified: true, ct);

    private async Task<BcConnectionTestResult> FetchAndUpsertEnvironmentsAsync(int projectId, bool markVerified, CancellationToken ct)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        return await RefreshEnvironmentsCoreAsync(project, markVerified, ct);
    }

    /// <summary>
    /// Re-reads a project's environments and re-mirrors their Business Central detail,
    /// deliberately <strong>not</strong> access-gated — the precedent is
    /// <see cref="AcquireDeliveryContextAsync"/>. The nightly update sweep runs this from
    /// a background worker where there is no acting user to gate against, under the
    /// project's own org scope so the EF query filter still applies. It never stamps
    /// <c>BcConnectionVerifiedAt</c>: a sweep the consultant never asked for must not
    /// present itself as their "Test connection" result.
    /// <para>
    /// Every caller reaching this from a request must gate first; the public entry points
    /// (<see cref="TestConnectionAsync"/>, <see cref="RefreshEnvironmentsAsync"/>) do.
    /// </para>
    /// </summary>
    public async Task<BcConnectionTestResult> RefreshEnvironmentsUnattendedAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");

        return await RefreshEnvironmentsCoreAsync(project, markVerified: false, ct);
    }

    /// <summary>
    /// The shared credential-resolve to token to list to upsert to mirror core, with no
    /// access check of its own. Callers decide the gate and whether the round-trip counts
    /// as a verification of the connection.
    /// </summary>
    private async Task<BcConnectionTestResult> RefreshEnvironmentsCoreAsync(OeProject project, bool markVerified, CancellationToken ct)
    {
        var projectId = project.Id;
        var creds = await ResolveCredentialsAsync(project, ct);
        if (creds is null)
        {
            return new BcConnectionTestResult(BcConnectionResult.AuthFailed, 0,
                "Enter the connection details (tenant, client ID, and secret) first.");
        }

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, forceRefresh: true, ct);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("BC test connection: token step failed for project {ProjectId}: {Message}.", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AuthFailed, 0,
                "The credentials were rejected. Check the tenant ID, client ID, and secret, then try again.");
        }

        IReadOnlyList<BcEnvironment> environments;
        try
        {
            environments = await _adminClient.ListEnvironmentsAsync(token, ct);
        }
        // 401 and 403 mean different things here and are fixed in different places, so
        // they get different messages. Entra having issued a token (we got past the step
        // above) tells us nothing about either: Business Central keeps its own list of
        // apps it will talk to.
        catch (BcApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("BC test connection: app not accepted by BC for project {ProjectId}. {Detail}", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AppNotAuthorized, 0,
                "Business Central didn't accept this app. In the Business Central admin center, "
                + "open 'Authorized Microsoft Entra apps', add the client ID above, and grant consent. Then test again.");
        }
        catch (BcApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("BC test connection: environments call denied for project {ProjectId}. {Detail}", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AccessDenied, 0,
                "The app is registered but isn't allowed to list environments. Check that it has the "
                + "AdminCenter.ReadWrite.All permission with admin consent. If this is a customer's tenant you manage "
                + "as a partner, check the delegated admin (GDAP) relationship too.");
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("BC test connection: environments call failed for project {ProjectId}: {Message}.", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.Error, 0,
                "Couldn't list the environments. " + ex.Message);
        }

        await UpsertEnvironmentsAsync(project, environments, ct);
        if (markVerified) project.BcConnectionVerifiedAt = DateTime.UtcNow;
        project.BcEnvironmentsFetchedAt = DateTime.UtcNow; // the refresh freshness gate reads this
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // After the save, so newly-discovered environments already have rows to mirror
        // onto, and so a failure here can never cost us the environment list itself.
        // A refresh in this scope that failed after reading leaves nothing for this one to announce.
        _scheduleChanges.Clear();
        await MirrorBcEnvironmentDetailsAsync(project, token, ct);
        await _db.SaveChangesAsync(ct);
        await AnnounceScheduleChangesAsync();

        _logger.LogInformation("BC test connection succeeded for project {ProjectId}: {Count} environment(s).", projectId, environments.Count);
        return new BcConnectionTestResult(BcConnectionResult.Success, environments.Count,
            environments.Count == 1 ? "Connected. Found 1 environment." : $"Connected. Found {environments.Count} environments.");
    }

    /// <summary>
    /// The project's fetched environments (the delivery targets). Production first, then
    /// sandboxes, name-ordered within each group: production is the one a consultant is
    /// looking for when something is wrong, and a customer often has several sandboxes
    /// that would otherwise bury it. Read-only.
    /// </summary>
    public async Task<IReadOnlyList<ProjectEnvironmentRow>> ListEnvironmentsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            // BC reports type as "Production"/"Sandbox"; compare lowered so a casing
            // change on their side can't silently flip the order.
            .OrderBy(e => e.Type.ToLower() == "production" ? 0 : 1)
            .ThenBy(e => e.Name)
            .Select(e => new ProjectEnvironmentRow(
                e.Id, e.Name, e.Type, e.FetchedAt, e.MissingSince,
                e.UpdateWindowStart, e.UpdateWindowEnd,
                e.Status,
                e.AppSourceAppsUpdateCadence,
                e.BcUpdateWindowStart, e.BcUpdateWindowEnd, e.BcUpdateWindowTimeZoneIana, e.BcUpdateWindowFetchedAt,
                e.Version, e.WebClientLoginUrl))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Everything the environment panel shows, fetched live: installed apps, available
    /// Marketplace app updates, scheduled per-tenant installs, and the platform updates
    /// coming to the environment. Readable by anyone who may see the solution; a
    /// <paramref name="forceRefresh"/> is a manager's call, because it is the one that
    /// makes the customer's tenant answer again.
    /// <para>
    /// <b>Cached for <see cref="BcPanelCache.Ttl"/>.</b> These are four reads against
    /// Business Central, and re-issuing them every time a consultant expands a row is
    /// traffic Microsoft's API does not need — especially as we honour no throttle. A
    /// window this short keeps the panel's promise of "what is true right now" while
    /// collapsing a working session's repeated opens into one fetch. Anything we write
    /// ourselves invalidates the entry (see <see cref="BcPanelCache.Invalidate"/>), so a
    /// consultant never reads a stale answer caused by their own action; a change made
    /// directly in Business Central is picked up by <paramref name="forceRefresh"/>.
    /// </para>
    /// <para>
    /// Each section fails on its own. One endpoint being denied — the app-management
    /// reads and the platform-update read are different permissions in practice — must
    /// not blank the other three, so a failure is carried as that section's message and
    /// the rest still render.
    /// </para>
    /// </summary>
    /// <param name="forceRefresh">Bypass the cache and re-read — what the panel's Refresh does.</param>
    public async Task<BcEnvironmentPanel> GetEnvironmentPanelAsync(
        int projectId, int environmentId, bool forceRefresh = false, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        // Looking is the view axis; making the customer's tenant answer again is not.
        await EnsureGateAsync(
            forceRefresh ? EnvironmentGate.Manage : EnvironmentGate.View,
            projectId, project.CreatedByUserId, ct);

        // Only now, with the organisation and access checks passed, may we look at the
        // cache: it is keyed by ids alone and knows nothing about who is allowed to read
        // them. Which apps are ours is re-read either way — that comes from our own
        // database, costs one cheap query, and means a delivery made since the cached
        // read still shows up as ours.
        if (!forceRefresh && _panelCache.Get(projectId, environmentId) is { } cached)
        {
            return cached with
            {
                ReleasedAppIds = await ReleasedAppIdsAsync(projectId, cached.EnvironmentName, ct),
            };
        }

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var creds = await ResolveCredentialsAsync(project, ct)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;

        // The four reads don't depend on each other, so they go out together and the panel
        // costs one round trip's wait rather than four. Nothing here touches the DbContext
        // — its work is done above and resumes below — so the scoped context is never used
        // concurrently. Task.WhenAll first so a non-BcApiException from one read can't
        // leave the other three unobserved.
        var installedTask = ReadSectionAsync(() => _apps.ListInstalledAppsAsync(token, family, env.Name, ct),
            "the installed apps", env.Name);
        var updatesTask = ReadSectionAsync(() => _apps.ListAvailableUpdatesAsync(token, family, env.Name, ct),
            "the available Marketplace app updates", env.Name);
        var scheduledTask = ReadSectionAsync(() => _apps.ListScheduledPteOperationsAsync(token, family, env.Name, ct),
            "the scheduled installs", env.Name);
        var platformTask = ReadSectionAsync(() => _adminClient.ListEnvironmentUpdatesAsync(token, family, env.Name, ct),
            "the Business Central updates", env.Name);
        await Task.WhenAll(installedTask, updatesTask, scheduledTask, platformTask);

        var installed = await installedTask;
        var updates = await updatesTask;
        var scheduled = await scheduledTask;
        var platform = await platformTask;

        var panel = new BcEnvironmentPanel(
            env.Name,
            await ReleasedAppIdsAsync(projectId, env.Name, ct),
            installed.Items, installed.Error,
            updates.Items, updates.Error,
            scheduled.Items, scheduled.Error,
            platform.Items, platform.Error,
            _clock.GetUtcNow().UtcDateTime);

        _panelCache.Set(projectId, environmentId, panel);

        // A successful read is also the freshest answer to "which modules does this
        // customer have", which people who cannot make this read still need to see.
        if (installed.Error is null && installed.Items.Count > 0)
        {
            try
            {
                await MirrorInstalledAppsAsync(project.OrganizationId, environmentId, installed.Items, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Couldn't mirror the installed apps of environment {EnvironmentId}.", environmentId);
            }
        }
        return panel;
    }

    /// <summary>
    /// Brings <c>oe_environment_apps</c> in line with what Business Central just reported,
    /// in place: changed rows are updated, gone ones removed, new ones added. The caller
    /// saves. Never called with an empty list - an environment always has the base
    /// application, so "nothing" is a failed read and must not wipe the last good one.
    /// See <c>.design/solution-customer-info.md</c>, "Modules".
    /// </summary>
    private async Task MirrorInstalledAppsAsync(
        int organizationId, int environmentId, IReadOnlyList<BcInstalledApp> apps, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.OeEnvironmentApps.Where(a => a.EnvironmentId == environmentId).ToListAsync(ct);
        var byId = existing.ToDictionary(a => a.AppId);
        var reported = new HashSet<Guid>();

        foreach (var app in apps)
        {
            if (!reported.Add(app.AppId)) continue;
            if (!byId.TryGetValue(app.AppId, out var row))
            {
                row = new OeEnvironmentApp { OrganizationId = organizationId, EnvironmentId = environmentId, AppId = app.AppId };
                _db.OeEnvironmentApps.Add(row);
            }
            row.Name = Truncate(app.Name, 250);
            row.Publisher = Truncate(app.Publisher, 250);
            row.Version = Truncate(app.Version, 50);
            row.FetchedAt = now;
        }

        _db.OeEnvironmentApps.RemoveRange(existing.Where(a => !reported.Contains(a.AppId)));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>
    /// Which of the apps in an environment this workbench has actually released there.
    /// Best-effort by app id, from the delivery history: enough to tell a consultant
    /// "this pending install is one of yours" instead of leaving them to recognise a
    /// publisher name.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ReleasedAppIdsAsync(
        int projectId, string environmentName, CancellationToken ct)
    {
        // A delivery snapshots the environment's name at the time it was scheduled, so a
        // soft-deleted environment — which Business Central renames, see the fold in
        // UpsertEnvironmentsAsync — would otherwise lose every release made to it before
        // the deletion. Its pre-deletion name is the same string with the stamp stripped,
        // so match that too.
        var formerName = SoftDeleteStampedBaseName(environmentName);

        var ids = await _db.OeProjectDeliveryResults.AsNoTracking()
            .Where(r => r.AppId != null
                        && r.ProjectDelivery!.ProjectId == projectId
                        && (r.ProjectDelivery.EnvironmentName == environmentName
                            || (formerName != null && r.ProjectDelivery.EnvironmentName == formerName)))
            .Select(r => r.AppId!)
            .Distinct()
            .ToListAsync(ct);

        return ids
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
    }

    /// <summary>
    /// Runs one panel read, turning a refusal into a message for that section instead of
    /// an exception that would take the whole panel down with it.
    /// </summary>
    private async Task<(IReadOnlyList<T> Items, string? Error)> ReadSectionAsync<T>(
        Func<Task<IReadOnlyList<T>>> read, string what, string environmentName)
    {
        try
        {
            return (await read().ConfigureAwait(false), null);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("Couldn't read {What} for environment {Environment}: {Message}.",
                what, environmentName, ex.Message);
            return (Array.Empty<T>(), $"Couldn't read {what} from Business Central. {ex.Message}");
        }
    }

    /// <summary>
    /// Which permission an environment write asks for. The two axes are deliberately
    /// separate (see <see cref="ProjectAccess.CanManageEnvironmentUpdatesAsync"/>):
    /// managing a project does not grant the update-ops flag, and holding the flag does
    /// not make somebody a project manager.
    /// </summary>
    private enum EnvironmentGate
    {
        /// <summary>Owner / org Admin / assigned-team manager — everything on the BC tab.</summary>
        Manage,

        /// <summary>
        /// Anyone who may see the solution — which on a Public or Read-only solution is
        /// everyone in the organisation, and on a Private one its teams. For the reads
        /// that only look: what is installed, what Business Central has been doing, who
        /// is signed in. See <c>.design/teams-and-visibility.md</c>.
        /// </summary>
        View,

        /// <summary>The environment-updates flag only — the fleet actions from issue #657.</summary>
        UpdateOps,

        /// <summary>Either will do: a project manager and an update-ops holder both have a reason to pick the next version.</summary>
        ManageOrUpdateOps,
    }

    /// <summary>
    /// Resolves the token and family for one environment, after checking the caller passes
    /// <paramref name="gate"/>. Every 5b write goes through here, so the access check and
    /// the "connection not set up" message live in one place.
    /// </summary>
    private async Task<(string Token, string Family, string Name, int Id)> ResolveEnvironmentAsync(
        int projectId, int environmentId, CancellationToken ct, EnvironmentGate gate = EnvironmentGate.Manage)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        await EnsureGateAsync(gate, projectId, project.CreatedByUserId, ct);

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Id, e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var creds = await ResolveCredentialsAsync(project, ct)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;
        return (token, family, env.Name, env.Id);
    }

    /// <summary>
    /// Runs one of the four access checks. The "either" case tries the project-manage
    /// axis first and falls back to the update-ops flag, so a refusal names both ways in.
    /// </summary>
    private async Task EnsureGateAsync(EnvironmentGate gate, int projectId, int? createdByUserId, CancellationToken ct)
    {
        switch (gate)
        {
            case EnvironmentGate.Manage:
                await _access.EnsureCanManageAsync(projectId, createdByUserId, ct);
                break;
            case EnvironmentGate.UpdateOps:
                await _access.EnsureCanManageEnvironmentUpdatesAsync(projectId, ct);
                break;
            case EnvironmentGate.View:
                await _access.EnsureCanViewAsync(projectId, ct);
                break;
            default:
                if (await _access.CanManageAsync(projectId, createdByUserId, ct)) break;
                if (await _access.CanManageEnvironmentUpdatesAsync(projectId, ct)) break;
                throw new ProjectAccessDeniedException(
                    "You need to manage this project, or hold permission to manage environment updates for one of its teams.");
        }
    }

    /// <summary>
    /// Sets how often Marketplace apps update on the environment, then refreshes the
    /// cached column so the page agrees with the tenant. The row write is what puts this
    /// in the audit log — see <c>AuditInterceptor.EnvironmentSettingColumns</c>.
    /// </summary>
    public async Task SetAppUpdateCadenceAsync(int projectId, int environmentId, string cadence, CancellationToken ct = default)
    {
        if (BcAppUpdateCadence.Normalize(cadence) is not { } value)
        {
            throw Validation("Cadence", "Choose how often Marketplace apps should update.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            await _adminClient.SetAppUpdateCadenceAsync(env.Token, env.Family, env.Name, value, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Cadence", ex.Message);
        }

        var row = await _db.OeProjectEnvironments.FirstAsync(e => e.Id == env.Id, ct);
        row.AppSourceAppsUpdateCadence = value;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What Business Central has done, or is doing, to the environment, newest first -
    /// whoever asked for it. Read live each time: the point of the list is to watch
    /// something finish, which a cache would hide.
    /// <para>
    /// Gated on seeing the solution, not on managing it: it only looks, and a consultant
    /// on a Public solution needs to know whether the install finished as much as its
    /// owner does. Anything that acts on the environment stays manage-gated.
    /// </para>
    /// </summary>
    public async Task<List<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.View);
        try
        {
            var operations = await _adminClient.ListEnvironmentOperationsAsync(env.Token, env.Family, env.Name, ct);
            return operations.OrderByDescending(o => o.CreatedOn ?? DateTimeOffset.MinValue).ToList();
        }
        catch (BcApiException ex)
        {
            throw Validation("Operations", ex.Message);
        }
    }

    /// <summary>
    /// Who is signed in to the environment right now, longest-running operation first -
    /// which is the one somebody on the phone about a locked posting run is looking for.
    /// <para>
    /// Read live every time and <b>never cached and never stored</b>. A user id and what
    /// that person is doing is personal data with no reason to outlive the screen it is
    /// on, and a cached answer would be wrong in a way a cached operations list is not:
    /// the whole question is who is signed in <em>now</em>. See
    /// <c>.design/environment-updates.md</c>, "Sessions".
    /// </para>
    /// <para>
    /// Gated on seeing the solution, like the other reads on the page: whoever the
    /// solution is open to is who may ask who is signed in. Ending one of those sessions
    /// is a different question and stays with the people who manage the solution - see
    /// <see cref="CancelEnvironmentSessionAsync"/>.
    /// </para>
    /// </summary>
    public async Task<List<BcSession>> ListEnvironmentSessionsAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.View);
        try
        {
            var sessions = await _adminClient.ListSessionsAsync(env.Token, env.Family, env.Name, ct);
            // Longest first, then whoever has been signed in longest: two sessions with
            // nothing running are ordered by the only other thing that distinguishes them.
            return sessions
                .OrderByDescending(s => s.CurrentOperationDuration ?? TimeSpan.Zero)
                .ThenBy(s => s.LogOnDate ?? DateTimeOffset.MaxValue)
                .ThenBy(s => s.SessionId)
                .ToList();
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }
    }

    /// <summary>
    /// Ends one session on the customer's environment - the errand behind "posting has
    /// been running for an hour and everything is locked". Gated on managing the solution
    /// and confirmed by name at the page, because somebody loses their unsaved work the
    /// moment it goes through.
    /// <para>
    /// The live list is re-read first, for two reasons. It is the only way the history
    /// line can name whose session it was and what it was running - the id alone answers
    /// nothing a week later, and the session list is never stored - and it turns "that
    /// session has already ended" into a sentence here rather than into a wire 404.
    /// </para>
    /// <para>
    /// Changes the customer's tenant and touches no row of ours, so it is recorded in the
    /// log and in the environment's Workbench history rather than the audit trail.
    /// </para>
    /// </summary>
    public async Task CancelEnvironmentSessionAsync(
        int projectId, int environmentId, int sessionId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        IReadOnlyList<BcSession> live;
        try
        {
            live = await _adminClient.ListSessionsAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }

        var session = live.FirstOrDefault(s => s.SessionId == sessionId)
            ?? throw Validation("Sessions",
                $"That session is no longer signed in to {env.Name}. Refresh the list to see who is.");

        try
        {
            await _adminClient.CancelSessionAsync(env.Token, env.Family, env.Name, sessionId, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }

        // The session's number and where - not whose it was or what it was running. Who
        // is signed in to a customer's system is shown and forgotten; the record of this
        // action is that we ended one, and which.
        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.CancelSession,
            $"Ended session {session.SessionId} on {env.Name}.", ct);

        _logger.LogInformation(
            "User {UserId} ended session {SessionId} ({ClientType}) on {Environment} (project {ProjectId}).",
            _orgContext.CurrentUserId, sessionId, session.ClientType, env.Name, projectId);
    }

    /// <summary>
    /// Reads whether Microsoft 365 licence access is on. Null when Business Central
    /// doesn't say (an environment too old to support it answers nothing useful).
    /// A read, so it follows the view axis; <see cref="SetM365AccessAsync"/> does not.
    /// </summary>
    public async Task<bool?> GetM365AccessAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.View);
        try
        {
            return await _adminClient.GetM365AccessAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns Microsoft 365 licence access on or off. Changes who can sign in to the
    /// customer's tenant and touches no row of ours, so it is recorded in the log rather
    /// than the audit trail — see <c>.design/saas-delivery.md</c>.
    /// </summary>
    public async Task SetM365AccessAsync(int projectId, int environmentId, bool enabled, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            await _adminClient.SetM365AccessAsync(env.Token, env.Family, env.Name, enabled, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("M365Access", ex.Message);
        }

        _logger.LogInformation(
            "User {UserId} set Microsoft 365 licence access to {Enabled} on {Environment} (project {ProjectId}).",
            _orgContext.CurrentUserId, enabled, env.Name, projectId);
    }

    /// <summary>
    /// Brings back an environment the customer deleted, while Business Central is still
    /// keeping it — the one write here that undoes somebody else's decision, so it is
    /// gated on managing the solution and confirmed by name at the page.
    /// <para>
    /// Refuses an environment that isn't deleted: there is nothing to recover, and
    /// Business Central would answer with a code rather than a sentence. Afterwards the
    /// customer's environments are re-read, because Microsoft schedules the recovery
    /// rather than doing it there and then and the row's state has to move on its own.
    /// A failed re-read costs the freshness, never the write.
    /// </para>
    /// <para>
    /// Changes the customer's tenant and touches no row of ours, so it is recorded in the
    /// log and in the environment's Workbench history rather than the audit trail — see
    /// <c>.design/environment-updates.md</c>, "Deleted environments".
    /// </para>
    /// </summary>
    public async Task RecoverEnvironmentAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        var row = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == env.Id)
            .Select(e => new { e.SoftDeletedOn, e.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        if (row.SoftDeletedOn is null && !BcEnvironmentStatus.IsSoftDeleted(row.Status))
        {
            throw Validation("Environment",
                $"{env.Name} hasn't been deleted, so there is nothing to bring back.");
        }

        try
        {
            await _adminClient.RecoverEnvironmentAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.RecoverEnvironment,
            $"Asked Business Central to bring {env.Name} back.", ct);

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} asked for the deleted environment {Environment} (project {ProjectId}) to be recovered.",
            _orgContext.CurrentUserId, env.Name, projectId);

        try
        {
            // Already gated above, which is what this entry point requires of its callers.
            await RefreshEnvironmentsUnattendedAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Recovered {Environment} but couldn't re-read project {ProjectId}'s environments afterwards.",
                env.Name, projectId);
        }
    }

    /// <summary>
    /// Copies an environment into a new one — almost always a customer's production into a
    /// fresh sandbox, to try an update or reproduce a problem on real data. Gated on
    /// managing the solution and confirmed by name at the page, because the copy adds an
    /// environment to the customer's tenant: it counts against their storage allowance,
    /// and a production copy against their licences.
    /// <para>
    /// Refuses before anything is sent when the source is one the customer has deleted, or
    /// one Business Central is not reporting as ready — a copy of an environment part-way
    /// through an update would be a copy of an unknown moment. The name is checked against
    /// Business Central's rules and against the names this solution already has, so the
    /// common mistake is answered here rather than as a wire code.
    /// </para>
    /// <para>
    /// Microsoft schedules the copy rather than making it there and then, so the customer's
    /// environments are re-read afterwards; the new one appears as <c>Preparing</c> once
    /// Business Central lists it, which may not be on this read. A failed re-read costs the
    /// freshness, never the write. Recorded in the log and in the <em>source</em>
    /// environment's Workbench history — see <c>.design/environment-updates.md</c>,
    /// "Copying an environment".
    /// </para>
    /// </summary>
    /// <returns>The operation Business Central scheduled, for the log and the Operations tab.</returns>
    public async Task<BcEnvironmentCopy> CopyEnvironmentAsync(
        int projectId, int sourceEnvironmentId, string newName, string targetType, CancellationToken ct = default)
    {
        if (BcEnvironmentName.Validate(newName) is { } nameProblem)
        {
            throw Validation("NewName", nameProblem);
        }
        if (BcEnvironmentTypes.Normalize(targetType) is not { } type)
        {
            throw Validation("TargetType", "Choose whether the copy is a sandbox or a production environment.");
        }
        var name = newName.Trim();

        var env = await ResolveEnvironmentAsync(projectId, sourceEnvironmentId, ct);

        var source = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == env.Id)
            .Select(e => new { e.SoftDeletedOn, e.Status, e.Type })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        if (source.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(source.Status))
        {
            throw Validation("Environment",
                $"{env.Name} has been deleted, so there is nothing to copy. Bring it back first.");
        }
        // The same reading the delivery gate makes: ready, or a status we have no opinion
        // about. Anything else is a moving target, and a copy of one is a copy of nothing
        // anybody can name.
        if (!BcEnvironmentStatus.CanPublish(source.Status))
        {
            throw Validation("Environment",
                $"Business Central reports {env.Name} as {BcEnvironmentStatus.Humanise(source.Status).ToLowerInvariant()} right now, "
                + "so it can't be copied. Wait until it is running again, then try again.");
        }

        // Our mirror, not Business Central's word - but it catches the common mistake
        // before a round trip, and says which environment is in the way.
        var taken = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.MissingSince == null)
            .Select(e => e.Name)
            .ToListAsync(ct);
        if (taken.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw Validation("NewName", $"This solution already has an environment called {name}. Pick another name.");
        }

        BcEnvironmentCopy copy;
        try
        {
            copy = await _adminClient.CopyEnvironmentAsync(env.Token, env.Family, env.Name, name, type, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.CopyEnvironment,
            $"Copied to {name}, a {type.ToLowerInvariant()} environment.", ct);

        _logger.LogInformation(
            "User {UserId} asked Business Central to copy {Environment} ({SourceType}) to {NewEnvironment} ({Type}) in project {ProjectId}; operation {OperationId} is {Status}.",
            _orgContext.CurrentUserId, env.Name, source.Type, name, type, projectId, copy.OperationId, copy.Status);

        try
        {
            // Already gated above, which is what this entry point requires of its callers.
            // The copy takes a while, so the new environment may not be listed yet - that
            // is not a failure, and the page says where to watch it instead.
            await RefreshEnvironmentsUnattendedAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Asked for a copy of {Environment} but couldn't re-read project {ProjectId}'s environments afterwards.",
                env.Name, projectId);
        }

        return copy;
    }

    /// <summary>
    /// Selects the platform version the environment updates to next — a reschedule of the
    /// customer's Business Central upgrade. Refuses a version the environment doesn't
    /// report as available, so a stale page can't schedule something Microsoft hasn't
    /// released, and refuses while an update is already running, which Microsoft owns.
    /// <para>
    /// Open to a project manager <em>or</em> someone holding the environment-updates flag
    /// on one of the project's teams: picking the version a customer moves to is the same
    /// job as moving its date, which the upgrade team owns (issue #657). The Upgrades
    /// page's "change the next version" fleet action calls this once per environment
    /// (issue #960).
    /// </para>
    /// <para>
    /// Usually no date is sent: Business Central keeps or assigns one inside the new
    /// version's rollout. The exception is a target update that already carries a date in
    /// the past, which Business Central refuses to select (issue #980); then a date goes
    /// with the selection, chosen by <see cref="DateForVersionChange"/>. Like the two date writes, the row is re-mirrored from a fresh read
    /// afterwards, and that read is also the proof - a read that still shows another
    /// version selected fails the call rather than being recorded as done (the #804
    /// lesson). The change is recorded in the audit log the same way.
    /// </para>
    /// </summary>
    public async Task SelectTargetVersionAsync(
        int projectId, int environmentId, string targetVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw Validation("TargetVersion", "Choose the version to update to.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.ManageOrUpdateOps);

        IReadOnlyList<BcEnvironmentUpdate> updates;
        try
        {
            updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("TargetVersion", "Couldn't read the versions available for this environment. " + ex.Message);
        }

        if (updates.Any(u => IsUpdateUnderWay(u.UpdateStatus)))
        {
            throw Validation("TargetVersion",
                $"An update is already running on {env.Name}, so its version can't be changed. Try again once it finishes.");
        }

        var chosen = updates.FirstOrDefault(u =>
            string.Equals(u.TargetVersion, targetVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null || !chosen.Available)
        {
            throw Validation("TargetVersion",
                $"Business Central {targetVersion} isn't available for {env.Name} right now. Reopen the panel to see what is.");
        }

        var before = PickNextUpdate(updates);
        var date = DateForVersionChange(chosen, before, _clock.GetUtcNow());

        try
        {
            // Never ignoreUpdateWindow: only "Start update" may take the window away.
            await _adminClient.SelectTargetVersionAsync(
                env.Token, env.Family, env.Name, chosen.TargetVersion, chosen.TargetVersionType,
                selectedDateTime: date, ct: ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("TargetVersion", ex.Message);
        }

        // We just changed what the panel's updates section says, so nobody should be
        // shown the answer we cached before this call.
        _panelCache.Invalidate(projectId, environmentId);

        await RemirrorAfterVersionChangeAsync(env, chosen.TargetVersion, ct);
        await RecordUpdateActionAsync(
            projectId, env, before, $"Set the next version to {chosen.TargetVersion}", ct);

        if (date is { } sent)
        {
            _logger.LogInformation(
                "User {UserId} scheduled Business Central {Version} as the next update for {Environment} (project {ProjectId}), sending {SelectedDateTime} because the update carried a past date ({StaleDateTime}).",
                _orgContext.CurrentUserId, chosen.TargetVersion, env.Name, projectId, sent, chosen.SelectedDateTime);
        }
        else
        {
            _logger.LogInformation(
                "User {UserId} scheduled Business Central {Version} as the next update for {Environment} (project {ProjectId}).",
                _orgContext.CurrentUserId, chosen.TargetVersion, env.Name, projectId);
        }
    }

    /// <summary>
    /// How far ahead of now a date must sit to count as "in the future" for a version
    /// change. A date a few seconds ahead when we read it is in the past by the time the
    /// PATCH lands, and Business Central refuses the selection then just as it would for
    /// yesterday's date.
    /// </summary>
    internal static readonly TimeSpan VersionChangeDateGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The date to send with a version change, or null to send none. Business Central
    /// refuses to select an update whose own stored date is already past ("Modify the
    /// selected date time first", issue #980), so only then does a date travel: the
    /// customer's current slot (<paramref name="current"/>) when it is still ahead and
    /// inside the target's bound, so the agreed day survives the version change; otherwise
    /// the target's last allowed date, which is where "Move dates" would put it. With no
    /// usable bound there is nothing we may send, so the change is refused with a message
    /// that sends the person to the admin centre. See <c>.design/environment-updates.md</c>,
    /// "The three writes".
    /// </summary>
    internal static DateTimeOffset? DateForVersionChange(
        BcEnvironmentUpdate target, BcEnvironmentUpdate? current, DateTimeOffset now)
    {
        var earliest = now + VersionChangeDateGrace;
        if (target.SelectedDateTime is not { } stale || stale > earliest) return null;

        var latest = BcUpdateSchedule.EffectiveLatest(target.LatestSelectableDateTime);
        if (latest is not { } bound || bound <= earliest)
        {
            throw Validation("TargetVersion",
                $"Business Central {target.TargetVersion} carries an update date that has already passed, and "
                + "Microsoft allows no later date to move it to from here. Set a new date for it in the Business Central "
                + "admin centre, then change the version again.");
        }

        if (current?.SelectedDateTime is { } agreed && agreed > earliest && agreed <= bound)
        {
            return agreed;
        }
        return bound;
    }

    /// <summary>
    /// True when Business Central reports the update as started. Its <c>updateStatus</c>
    /// is compared case-insensitively and never shown, per the mirror's rule that
    /// Microsoft's spelling drives no logic beyond a token compare. Shared with the fleet
    /// preview, which reads the same value from the mirror.
    /// </summary>
    internal static bool IsUpdateUnderWay(string? updateStatus) =>
        string.Equals(updateStatus?.Trim(), "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-reads the environment after a version change and writes the mirror, so the
    /// Upgrades page shows the new version without waiting for the nightly sweep. A read
    /// that fails only costs the freshness; a read that succeeds and shows a different
    /// version still selected fails the call, because saying "done" for a change that did
    /// not land is worse than saying nothing.
    /// </summary>
    private async Task RemirrorAfterVersionChangeAsync(
        (string Token, string Family, string Name, int Id) env, string targetVersion, CancellationToken ct)
    {
        BcEnvironmentUpdate? stored;
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            stored = PickNextUpdate(updates);
            var row = await _db.OeProjectEnvironments.FirstOrDefaultAsync(e => e.Id == env.Id, ct);
            if (row is not null)
            {
                ApplyNextUpdate(row, updates);
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "The next version on {Environment} was changed, but re-reading it failed: {Message}. The cached row stays stale until the next refresh.",
                env.Name, ex.Message);
            return;
        }

        if (stored is not null && CompareVersions(stored.TargetVersion, targetVersion) == 0) return;

        _logger.LogWarning(
            "Business Central kept {Environment} on {StoredVersion} after being asked for {TargetVersion}, so the change was not recorded as done.",
            env.Name, stored?.TargetVersion, targetVersion);
        throw Validation("TargetVersion", stored is not null
            ? $"Business Central did not accept the new version. Its next update is still {stored.TargetVersion}."
            : "Business Central did not accept the new version. It has no next update chosen.");
    }

    /// <summary>
    /// Moves the environment's next platform update to the latest date Microsoft still
    /// allows — the routine sweep the upgrade team runs across every customer before a
    /// release lands (issue #657). Gated on the environment-updates flag, not on managing
    /// the project. Refuses when there is no update to move, when Business Central gives
    /// the update no latest date, and when the date is already there, each with a message
    /// a fleet page can show against the row.
    /// </summary>
    public async Task PushUpdateDateToLatestAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.UpdateOps);
        var next = await ReadNextUpdateAsync(env, ct)
            ?? throw Validation("Update", "No update is available to reschedule.");

        if (BcUpdateSchedule.EffectiveLatest(next.LatestSelectableDateTime) is not { } latest)
        {
            throw Validation("Update", "Business Central hasn't given this update a last possible date, so it can't be moved.");
        }
        // On or after, by calendar day in UTC rather than by tick: Business Central stores
        // the date at the start of the environment's own update window, so an update that
        // is already as late as it can go reads back a different time of day from the one
        // we would send — and a window starting after midnight UTC (02:00 in Copenhagen is
        // 01:00 UTC) lands it on the following day. Either way there is nowhere left to
        // move it to, and re-sending would only fail against the bound.
        if (next.SelectedDateTime?.UtcDateTime.Date >= latest.UtcDateTime.Date)
        {
            throw Validation("Update", "This update's date is already the latest Microsoft allows.");
        }

        await WriteUpdateScheduleAsync(env, next, latest, ignoreUpdateWindow: null, ct, verifyDateMoved: true);
        await RecordUpdateActionAsync(
            projectId, env, next, "Moved the update date out to the latest Business Central allows", ct);
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} pushed the Business Central {Version} update on {Environment} (project {ProjectId}) out to {SelectedDateTime}.",
            _orgContext.CurrentUserId, next.TargetVersion, env.Name, projectId, latest);
    }

    /// <summary>
    /// Starts the environment's next platform update as soon as Business Central will take
    /// it: the date is set to now and the environment's update window is ignored, which is
    /// what a customer who has agreed a slot is asking for. This is the only operation that
    /// ever ignores the window. Gated on the environment-updates flag; refuses when there
    /// is no update to run.
    /// </summary>
    public async Task RunUpdateNowAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.UpdateOps);
        var next = await ReadNextUpdateAsync(env, ct)
            ?? throw Validation("Update", "No update is available to run.");

        var now = DateTimeOffset.UtcNow;
        await WriteUpdateScheduleAsync(env, next, now, ignoreUpdateWindow: true, ct);
        await RecordUpdateActionAsync(
            projectId, env, next, "Started the update now, ignoring the environment's update window", ct);
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} started the Business Central {Version} update on {Environment} (project {ProjectId}) at {SelectedDateTime}, ignoring the update window.",
            _orgContext.CurrentUserId, next.TargetVersion, env.Name, projectId, now);
    }

    /// <summary>
    /// Re-reads one environment while an update runs on it, and re-mirrors that one row:
    /// its state and version, and its next update. What the Upgrades page's watch calls
    /// every ten seconds after "Start update" (issue #982), so a person can see the update
    /// through to the end without pressing Refresh.
    /// <para>
    /// Deliberately narrow: two requests against one tenant - the environment by name and
    /// its updates list - where a Refresh makes the whole tenant answer for every
    /// environment it has. The row is written through the same mapping the environment
    /// list uses (<see cref="ApplyFetched"/>, <see cref="ApplyNextUpdate"/>), so a watched
    /// row and a refreshed one cannot disagree about what a field means. See
    /// <c>.design/environment-updates.md</c>, "The page", for why this load is a fine
    /// guest on Microsoft's API.
    /// </para>
    /// <para>
    /// Gated on the environment-updates grant, the same one the Refresh on that page
    /// queues under and the one that started the update. Both reads must answer or nothing
    /// is written: a status without its update would let the page call an update over that
    /// is still running. A read that fails comes back under the <c>Refresh</c> key, which a
    /// caller can retry; an environment that is gone, or a connection that needs setting
    /// up, comes back under <c>Environment</c>, which retrying will not fix.
    /// </para>
    /// </summary>
    /// <returns>What the row now says, for the page to show without reading the fleet again.</returns>
    public async Task<BcEnvironmentReading> RefreshEnvironmentAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.UpdateOps);

        BcEnvironment? fetched;
        IReadOnlyList<BcEnvironmentUpdate> updates;
        try
        {
            fetched = await _adminClient.GetEnvironmentAsync(env.Token, env.Family, env.Name, ct);
            updates = fetched is null
                ? Array.Empty<BcEnvironmentUpdate>()
                : await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "Couldn't re-read {Environment} (project {ProjectId}) while watching its update: {Message}.",
                env.Name, projectId, ex.Message);
            throw Validation("Refresh", "Couldn't read the environment from Business Central. " + ex.Message);
        }

        if (fetched is null)
        {
            throw Validation("Environment", "Business Central no longer has this environment.");
        }

        var row = await _db.OeProjectEnvironments.FirstOrDefaultAsync(e => e.Id == env.Id, ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var now = _clock.GetUtcNow().UtcDateTime;
        ApplyFetched(row, fetched, now);
        _scheduleChanges.Clear();
        ApplyReadNextUpdate(row, updates);
        await _db.SaveChangesAsync(ct);
        await AnnounceScheduleChangesAsync();

        // What the panel cached before this read is now older than the row.
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "Re-read {Environment} (project {ProjectId}) while watching its update: {Status}, version {Version}, next update {NextVersion} {NextStatus}.",
            env.Name, projectId, row.Status, row.Version, row.BcNextUpdateVersion, row.BcNextUpdateStatus);

        return new BcEnvironmentReading(
            row.Status,
            row.Version,
            row.FetchedAt,
            row.BcNextUpdateVersion,
            row.BcNextUpdateType,
            row.BcNextUpdateStatus,
            row.BcNextUpdateDate,
            row.BcNextUpdateLatestDate,
            row.BcNextUpdateIgnoresWindow,
            row.BcOfferedVersions,
            row.BcNextUpdateFetchedAt);
    }

    /// <summary>
    /// Reads the environment's updates live and picks the one a date write acts on — the
    /// same rule the mirror caches, so the fleet page and the write agree on which update
    /// "the next update" is. Null when the environment has nothing on offer.
    /// </summary>
    private async Task<BcEnvironmentUpdate?> ReadNextUpdateAsync(
        (string Token, string Family, string Name, int Id) env, CancellationToken ct)
    {
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            return PickNextUpdate(updates);
        }
        catch (BcApiException ex)
        {
            throw Validation("Update", "Couldn't read the updates for this environment. " + ex.Message);
        }
    }

    /// <summary>
    /// Sends the date write and re-mirrors the row from a fresh read, so the fleet page
    /// shows the new date without waiting for the nightly sweep. The PATCH also selects
    /// the update, which matters when the picked one was merely available: setting a date
    /// on it is the customer choosing it.
    /// <para>
    /// With <paramref name="verifyDateMoved"/> the re-read is also the proof that the
    /// write landed. Issue #804 saw the move recorded as done while the date stayed exactly
    /// where it was, so the write is verified rather than trusted, and a history entry
    /// saying "done" for a date that never moved is worse than no entry at all. The test is
    /// whether the date <em>changed</em>, not whether it landed where we asked: Business
    /// Central puts it at the start of the customer's update window, which can be the
    /// following UTC day. A re-read that <em>fails</em> still only costs the freshness — it
    /// is a re-read that succeeds with an unchanged date that fails the action.
    /// </para>
    /// </summary>
    private async Task WriteUpdateScheduleAsync(
        (string Token, string Family, string Name, int Id) env,
        BcEnvironmentUpdate update,
        DateTimeOffset selectedDateTime,
        bool? ignoreUpdateWindow,
        CancellationToken ct,
        bool verifyDateMoved = false)
    {
        try
        {
            await _adminClient.SelectTargetVersionAsync(
                env.Token, env.Family, env.Name, update.TargetVersion, update.TargetVersionType,
                selectedDateTime, ignoreUpdateWindow, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Update", ex.Message);
        }

        // Re-read rather than assume: Business Central decides what it actually stored,
        // and a mirror that says what we asked for would be a guess. A failure here loses
        // the freshness, never the write.
        BcEnvironmentUpdate? stored;
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            stored = PickNextUpdate(updates);
            var row = await _db.OeProjectEnvironments.FirstOrDefaultAsync(e => e.Id == env.Id, ct);
            if (row is not null)
            {
                ApplyNextUpdate(row, updates);
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "The update date on {Environment} was changed, but re-reading it failed: {Message}. The cached row stays stale until the next refresh.",
                env.Name, ex.Message);
            return;
        }

        if (!verifyDateMoved) return;

        // Did it move, not did it land where we asked: the stored date is the start of the
        // customer's update window, so a window opening after midnight UTC legitimately
        // puts it on the day after the one we sent. Two nulls count as unchanged.
        if (stored?.SelectedDateTime != update.SelectedDateTime) return;

        var landed = stored?.SelectedDateTime?.UtcDateTime;
        _logger.LogWarning(
            "Business Central kept {Environment} on {StoredDateTime} after being asked for {SelectedDateTime}, so the move was not recorded as done.",
            env.Name, landed, selectedDateTime);

        throw Validation("Update", landed is { } value
            ? $"Business Central did not accept the new date. Its schedule still says {value:yyyy-MM-dd}."
            : "Business Central did not accept the new date. Its schedule still has no date.");
    }

    /// <summary>
    /// Records one fleet update action in the audit log. These three writes act on a
    /// <em>customer's production tenant</em> and touch no row of ours that the
    /// interceptor watches — the re-mirror afterwards is deliberately outside
    /// <c>AuditInterceptor.EnvironmentSettingColumns</c>, because the nightly sweep
    /// writes the same columns and would otherwise fill the log with rows nobody made.
    /// So the entry is written here, explicitly, and it is the only place in the
    /// application that writes to <c>audit_log</c> directly. See issue #657.
    ///
    /// <para>The snapshot keeps the log's "state before the change" contract: it is what
    /// the update looked like when we read it, plus a plain-words <c>Action</c> naming
    /// which of the three writes this was — the audit model records rows changing, and
    /// these are events, so the event has to be spelled out in the row itself. Two of
    /// these rows on one environment diff against each other cleanly, which is what the
    /// audit diff page reads.</para>
    ///
    /// <para>The actor is resolved from the database rather than from claims because
    /// this runs inside a Blazor circuit, where the interceptor's own
    /// <c>HttpContext</c> lookup has nothing to read.</para>
    /// </summary>
    private async Task RecordUpdateActionAsync(
        int projectId,
        (string Token, string Family, string Name, int Id) env,
        BcEnvironmentUpdate? update,
        string action,
        CancellationToken ct)
    {
        var changedBy = await ResolveActorAsync(ct);
        var projectName = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        var snapshot = new Dictionary<string, object?>
        {
            ["Action"] = action,
            ["Project"] = projectName,
            ["Name"] = env.Name,
            ["UpdateVersion"] = update?.TargetVersion,
            ["UpdateDate"] = update?.SelectedDateTime?.UtcDateTime,
            ["LatestPossibleDate"] = update?.LatestSelectableDateTime?.UtcDateTime,
            ["IgnoresUpdateWindow"] = update?.IgnoreUpdateWindow,
        };

        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ChangedBy = changedBy,
            ChangedByUserId = _orgContext.CurrentUserId,
            OrganizationId = _orgContext.CurrentOrganizationId,
            EntityType = AuditEntityType.ProjectEnvironment,
            EntityId = env.Id,
            Action = AuditAction.Updated,
            EntityName = env.Name,
            SnapshotJson = JsonSerializer.Serialize(snapshot, PersistenceJson.Options),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The acting user in the audit log's <c>"display name &lt;email&gt;"</c> form.
    /// Cached for the scope: a bulk run calls this once per row. The lookup itself is
    /// <see cref="AuditActor"/>, shared with the upgrade-action feed so one environment's
    /// history names a person the same way whichever route wrote the row.
    /// </summary>
    private async Task<string> ResolveActorAsync(CancellationToken ct) =>
        _actor ??= await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct);

    private string? _actor;

    /// <summary>
    /// Cancels one per-tenant extension version that Business Central has scheduled but
    /// not yet installed — the action that makes a handed-off delivery undoable.
    /// Access-gated. This permanently removes the uploaded package from Business Central,
    /// so releasing that version again means uploading it again.
    /// </summary>
    public async Task CancelScheduledInstallAsync(
        int projectId, int environmentId, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists.");

        var creds = await ResolveCredentialsAsync(project, ct)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;

        try
        {
            await _apps.RemoveScheduledPteVersionAsync(token, family, env.Name, appId, targetVersion, scheduleKind, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", "Business Central didn't cancel the scheduled install. " + ex.Message);
        }

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "Cancelled the scheduled install of app {AppId} version {Version} ({ScheduleKind}) on {Environment} (project {ProjectId}).",
            appId, targetVersion, scheduleKind, env.Name, projectId);
    }

    /// <summary>
    /// Books one AppSource app to be updated to the version Business Central has waiting
    /// for it. Manage-gated, like the other writes that are not about the platform update.
    /// <para>
    /// Every timing is a booking, as for an uploaded app: one <c>Pending</c> row of kind
    /// <see cref="UpgradeActionKind.UpdateApp"/> that <see cref="UpgradeActionWorker"/>
    /// sends as an immediate update when the slot arrives, waiting for it to finish, in
    /// turn with the uploads. Business Central's own <c>UpdateWindow</c> schedule is never
    /// handed over, so the workbench keeps the promise that no two installs on an
    /// environment overlap (see <c>.design/saas-delivery.md</c>, "Updating an AppSource
    /// app"). The slot is worked out as for an upload.
    /// </para>
    /// <para>
    /// The version is not taken on trust. The waiting updates are read now, and the
    /// booking is made only for an app that is on that list, at exactly that version - so
    /// a stale page, or a caller that is not the page, cannot book a version Business
    /// Central never offered. They are read again when the booking is sent.
    /// </para>
    /// <para>
    /// An app that waits for others is updated together with them, but only the ones in
    /// <paramref name="confirmedPrerequisiteAppIds"/>: the apps somebody was shown and
    /// agreed to. The ones Business Central listed are stored on the booking, and if it
    /// asks for one outside that set - now or at send time - the update is refused, so
    /// nobody's agreement covers an app they never saw.
    /// </para>
    /// </summary>
    /// <param name="at">The slot for <see cref="UploadAppTiming.AtTime"/>; ignored for the other timings.</param>
    /// <param name="confirmedPrerequisiteAppIds">The apps the caller agreed may be installed or updated alongside; null or empty for none.</param>
    /// <returns>The timing that applied and the slot the update waits for.</returns>
    public async Task<UploadAppOutcome> BookAppUpdateAsync(
        int projectId, int environmentId, Guid appId, string targetVersion, UploadAppTiming timing,
        DateTimeOffset? at = null, IReadOnlyCollection<Guid>? confirmedPrerequisiteAppIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw Validation("App", "Choose the version to update to.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        var confirmed = confirmedPrerequisiteAppIds ?? Array.Empty<Guid>();
        // The slot first: a time that has gone is refused without asking Business Central anything.
        var now = _clock.GetUtcNow().UtcDateTime;
        var (applied, runsAt) = await ResolveBookingSlotAsync(environmentId, timing, at, now, ct);

        BcAvailableAppUpdate offered;
        try
        {
            offered = await ReadOfferedUpdateAsync(env, appId, targetVersion, confirmed, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("App", "Business Central didn't list the waiting updates. " + ex.Message);
        }

        // One booking per app, counting the apps a booking brings along: a second would
        // only ask Business Central for the same update twice, and whichever ran first
        // would leave the other failing on an update that is no longer waiting.
        var covered = offered.Requirements.Select(r => r.AppId!.Value).Append(appId).ToList();
        var clash = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.EnvironmentId == env.Id && a.Kind == UpgradeActionKind.UpdateApp
                        && a.Status == UpgradeActionStatus.Pending
                        && ((a.BcAppId != null && covered.Contains(a.BcAppId.Value))
                            || (a.PrerequisiteAppIds != null && a.PrerequisiteAppIds.Any(p => covered.Contains(p)))))
            .Select(a => new { a.BcAppId, a.AppName })
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
        {
            throw Validation("App", clash.BcAppId == appId
                ? $"An update of {offered.Name} is already booked for {env.Name}. Cancel it under Scheduled installs to book a different time."
                : $"The update of {clash.AppName ?? "another app"} already booked for {env.Name} moves {offered.Name} or an app it waits for. Cancel that booking under Scheduled installs first.");
        }

        _db.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
        {
            OrganizationId = RequireOrganizationId(),
            ProjectId = projectId,
            EnvironmentId = env.Id,
            Kind = UpgradeActionKind.UpdateApp,
            Status = UpgradeActionStatus.Pending,
            RequestedByUserId = _orgContext.CurrentUserId,
            RequestedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
            RequestedAt = now,
            ExecuteAfter = runsAt,
            BcAppId = appId,
            AppName = Truncate(offered.Name, 250),
            TargetVersion = offered.Version,
            // What the person was shown, and so what the send may bring along. The check
            // above has made sure each one has an id and was agreed to.
            PrerequisiteAppIds = offered.Requirements.Select(r => r.AppId!.Value).ToList(),
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} booked app {AppId} ({AppName}) to be updated to {Version} on {Environment} (project {ProjectId}) at {ExecuteAfter} ({Timing}), along with {PrerequisiteCount} prerequisites.",
            _orgContext.CurrentUserId, appId, offered.Name, offered.Version, env.Name, projectId, runsAt, applied, offered.Requirements.Count);
        return new UploadAppOutcome(applied, runsAt, 1);
    }

    /// <summary>
    /// Sends one booked AppSource update when its slot arrives and waits for Business
    /// Central to finish it, re-checking the requester's access, the connection and the
    /// waiting updates now rather than trusting what held at booking time. Refused in
    /// plain words if the waiting version has changed or Business Central now asks for a
    /// prerequisite outside <paramref name="agreedPrerequisiteAppIds"/>. Always sent to
    /// run now: the booking is the schedule. The caller (<see cref="UpgradeActionWorker"/>)
    /// records the outcome on the booking's own row.
    /// </summary>
    /// <param name="accepted">Called with the operation the moment Business Central accepts the update, before it is polled, as for an upload.</param>
    /// <param name="progress">Called on each poll, so a worker can keep its heartbeat alive through a long one.</param>
    internal async Task<BcAppOperationResult> SendBookedUpdateAsync(
        int projectId, int environmentId, Guid appId, string targetVersion, IReadOnlyCollection<Guid> agreedPrerequisiteAppIds,
        Func<BcAppOperation, CancellationToken, Task>? accepted, CancellationToken ct, Action? progress = null)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        BcAppOperation operation;
        try
        {
            var offered = await ReadOfferedUpdateAsync(env, appId, targetVersion, agreedPrerequisiteAppIds, ct);
            operation = await _apps.UpdateAppAsync(env.Token, env.Family, env.Name, appId, offered.Version, useEnvironmentUpdateWindow: false,
                installOrUpdateNeededDependencies: offered.Requirements.Count > 0, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("App", "Business Central didn't start the update. " + ex.Message);
        }

        _panelCache.Invalidate(projectId, environmentId);
        _logger.LogInformation(
            "User {UserId} updated app {AppId} to {Version} on {Environment} (project {ProjectId}), along with {PrerequisiteCount} agreed prerequisites; operation {OperationId}.",
            _orgContext.CurrentUserId, appId, targetVersion, env.Name, projectId, agreedPrerequisiteAppIds.Count, operation.Id);
        if (accepted is not null)
        {
            await accepted(operation, ct);
        }

        // Business Central may answer without the app id; it is the one we asked about.
        var polled = operation.AppId is null ? operation with { AppId = appId } : operation;
        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            _apps, env.Token, env.Family, env.Name, polled, UploadPollDelay, UploadPollTimeout, ct, progress);
        _panelCache.Invalidate(projectId, environmentId);
        if (result.Raw is { } raw)
        {
            _logger.LogWarning("Business Central reported the update of app {AppId} on {Environment} as failed: {Raw}", appId, env.Name, raw);
        }
        return result;
    }

    /// <summary>
    /// The waiting update for <paramref name="appId"/>, read live, when it is still at
    /// <paramref name="targetVersion"/> and every prerequisite Business Central lists is
    /// one in <paramref name="confirmed"/>. Throws the refusal in plain words otherwise.
    /// Shared by the booking and the send, so a slot fired tonight is held to what the
    /// person agreed to this afternoon.
    /// </summary>
    private async Task<BcAvailableAppUpdate> ReadOfferedUpdateAsync(
        (string Token, string Family, string Name, int Id) env, Guid appId, string targetVersion, IReadOnlyCollection<Guid> confirmed, CancellationToken ct)
    {
        var waiting = await _apps.ListAvailableUpdatesAsync(env.Token, env.Family, env.Name, ct);
        var offered = waiting.FirstOrDefault(u => u.AppId == appId)
            ?? throw Validation("App", "Business Central no longer has an update waiting for that app. Refresh and look again.");
        if (!string.Equals(offered.Version, targetVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw Validation("App", $"Business Central now offers {offered.Name} {offered.Version}, not {targetVersion}. Refresh and try again.");
        }
        var unconfirmed = offered.Requirements
            .Where(r => r.AppId is not { } id || !confirmed.Contains(id))
            .ToList();
        if (unconfirmed.Count > 0)
        {
            throw Validation("App", confirmed.Count == 0
                ? $"{offered.Name} has to wait for {string.Join(", ", unconfirmed.Select(r => r.Name))} to be updated first."
                : $"{offered.Name} now also waits for {string.Join(", ", unconfirmed.Select(r => r.Name))}. Refresh and look again.");
        }
        return offered;
    }

    /// <summary>
    /// Books extension packages somebody was handed - apps another company built, which
    /// have no pipeline here to release them from - to be installed on an environment.
    /// Manage-gated.
    /// <para>
    /// Every timing is a booking: one <c>Pending</c> row per app in
    /// <c>oe_environment_upgrade_actions</c> carrying the package, which
    /// <see cref="UpgradeActionWorker"/> sends when the slot arrives - "now" is a slot
    /// that has already come. Nothing is handed to Business Central with a deferred
    /// schedule, even for <see cref="UploadAppTiming.BcUpdateWindow"/>: the workbench
    /// books the next opening of Microsoft's window from the hours mirrored on the
    /// environment and sends then. That is what lets several apps go in order, each
    /// waiting for the one before it to finish installing, which the API's own queue
    /// could not promise (see <c>.design/saas-delivery.md</c>, "Uploading an app").
    /// </para>
    /// <para>
    /// The delivery window falls back to Microsoft's window when the environment has
    /// none. Microsoft's window has to have been read from the admin centre; until it
    /// has, that timing is refused rather than guessed. The sync mode is always Add and
    /// dependencies are never pulled along: a missing one is refused by name at send
    /// time, so nothing is installed that nobody picked.
    /// </para>
    /// </summary>
    /// <param name="packages">The apps, in the order to install them - dependencies first.</param>
    /// <param name="at">The slot for <see cref="UploadAppTiming.AtTime"/>; ignored for the other timings.</param>
    /// <returns>The timing that applied and the slot the batch waits for.</returns>
    public async Task<UploadAppOutcome> InstallUploadedAppsAsync(
        int projectId, int environmentId, IReadOnlyList<UploadPackage> packages, UploadAppTiming timing,
        DateTimeOffset? at = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
        {
            throw Validation("App", "Choose at least one extension package - a file ending in .app.");
        }
        if (packages.Count > MaxUploadBatch)
        {
            throw Validation("App", $"Choose at most {MaxUploadBatch} apps at a time.");
        }
        var cleaned = new List<(string Name, byte[] Bytes)>(packages.Count);
        foreach (var package in packages)
        {
            ArgumentNullException.ThrowIfNull(package.Bytes);
            var name = CleanUploadName(package.FileName);
            if (!name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                throw Validation("App", $"{(name.Length == 0 ? "One of the files" : name)} isn't an extension package - choose files ending in .app.");
            }
            if (package.Bytes.Length == 0)
            {
                throw Validation("App", $"{name} is empty.");
            }
            if (package.Bytes.Length > BcAppManagementClient.MaxAppBytes)
            {
                throw Validation("App", $"{name} is over 50 MB, which is the largest app Business Central accepts.");
            }
            cleaned.Add((name, package.Bytes));
        }

        // Resolved for a booking too: a slot for tonight that cannot get a token should
        // fail now, at the form, not at 20:00 in the worker.
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        var now = _clock.GetUtcNow().UtcDateTime;

        var (applied, runsAt) = await ResolveBookingSlotAsync(environmentId, timing, at, now, ct);

        var requestedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct);
        var batchId = cleaned.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        for (var i = 0; i < cleaned.Count; i++)
        {
            _db.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
            {
                OrganizationId = RequireOrganizationId(),
                ProjectId = projectId,
                EnvironmentId = env.Id,
                Kind = UpgradeActionKind.UploadApp,
                Status = UpgradeActionStatus.Pending,
                RequestedByUserId = _orgContext.CurrentUserId,
                RequestedBy = requestedBy,
                RequestedAt = now,
                ExecuteAfter = runsAt,
                PackageFileName = cleaned[i].Name,
                PackageContent = cleaned[i].Bytes,
                BatchId = batchId,
                BatchOrder = batchId is null ? null : i,
            });
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} booked {Count} app(s) ({Files}) for {Environment} (project {ProjectId}) at {ExecuteAfter} ({Timing}).",
            _orgContext.CurrentUserId, cleaned.Count, string.Join(", ", cleaned.Select(c => c.Name)), env.Name, projectId, runsAt, applied);
        return new UploadAppOutcome(applied, runsAt, cleaned.Count);
    }

    /// <summary>
    /// The instant a booked install waits for, and the timing that applied: a picked time
    /// as given (refused if it has gone), the delivery window's next opening, or - when
    /// the environment has none, or it was asked for - the next opening of Microsoft's
    /// update window from the hours mirrored on the environment (refused until read).
    /// Shared by uploads and AppSource updates, so both book the same slot for the same
    /// answer.
    /// </summary>
    private async Task<(UploadAppTiming Applied, DateTime RunsAtUtc)> ResolveBookingSlotAsync(
        int environmentId, UploadAppTiming timing, DateTimeOffset? at, DateTime now, CancellationToken ct)
    {
        var windows = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => new
            {
                e.UpdateWindowStart, e.UpdateWindowEnd, e.Project!.BcTimeZone,
                e.BcUpdateWindowStart, e.BcUpdateWindowEnd, e.BcUpdateWindowTimeZoneIana,
            })
            .FirstAsync(ct);

        var applied = timing;
        DateTime runsAt;
        switch (timing)
        {
            case UploadAppTiming.AtTime:
            {
                if (at is not { } slot)
                {
                    throw Validation("ExecuteAt", "Pick a time for the install.");
                }
                if (slot.UtcDateTime < now - BookingPastSlack)
                {
                    throw Validation("ExecuteAt", "Pick a time that hasn't happened yet.");
                }
                runsAt = slot.UtcDateTime;
                break;
            }
            case UploadAppTiming.DeliveryWindow when UpdateWindow.IsConfigured(windows.UpdateWindowStart, windows.UpdateWindowEnd):
                runsAt = UpdateWindow.NextOpeningUtc(
                    windows.UpdateWindowStart, windows.UpdateWindowEnd, UpdateWindow.ResolveTimeZone(windows.BcTimeZone), now);
                break;
            case UploadAppTiming.DeliveryWindow:
            case UploadAppTiming.BcUpdateWindow:
            {
                applied = UploadAppTiming.BcUpdateWindow;
                if (!UpdateWindow.IsConfigured(windows.BcUpdateWindowStart, windows.BcUpdateWindowEnd))
                {
                    throw Validation("Timing", timing == UploadAppTiming.DeliveryWindow
                        ? "This environment has no delivery window, and Business Central's own update window hasn't been read yet. Refresh the environment and try again, or pick a time."
                        : "Business Central's update window hasn't been read for this environment yet. Refresh the environment and try again, or pick a time.");
                }
                // Microsoft's window is expressed in its own zone; the solution's is the
                // fallback for a Windows zone id that had no IANA mapping.
                var zone = UpdateWindow.ResolveTimeZone(windows.BcUpdateWindowTimeZoneIana ?? windows.BcTimeZone);
                runsAt = UpdateWindow.NextOpeningUtc(windows.BcUpdateWindowStart, windows.BcUpdateWindowEnd, zone, now);
                break;
            }
            default:
                runsAt = now;
                break;
        }

        return (applied, runsAt);
    }

    /// <summary>The most apps one upload may book together. A vendor's suite, not a whole environment.</summary>
    public const int MaxUploadBatch = 10;

    /// <summary>
    /// Sends one booked upload when its slot arrives and waits for Business Central to
    /// finish installing it, re-checking the requester's access and the connection now
    /// rather than trusting what held at booking time. The caller
    /// (<see cref="UpgradeActionWorker"/>) records the outcome on the booking's own row,
    /// so nothing is recorded here beyond the log line and the panel cache.
    /// </summary>
    /// <param name="accepted">
    /// Called with the operation the moment Business Central accepts the package, before
    /// the install is polled: the caller stamps it on the row, so a restart during the
    /// poll knows the app is with Business Central rather than never sent.
    /// </param>
    /// <param name="progress">Called on each poll of the install, so a worker can keep its heartbeat alive through a long one.</param>
    internal async Task<BcAppOperationResult> SendBookedUploadAsync(
        int projectId, int environmentId, byte[] appBytes, string fileName,
        Func<BcAppOperation, CancellationToken, Task>? accepted, CancellationToken ct, Action? progress = null)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        BcAppOperation operation;
        try
        {
            operation = await _apps.InstallPteAsync(
                env.Token, env.Family, env.Name, appBytes, fileName,
                BcDeploymentSchedule.Immediate,
                BcSyncMode.Add,
                // No language, for the reason a delivery sends none: see DeliveryService.
                languageId: string.Empty,
                installOrUpdateNeededDependencies: false,
                ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("App", "Business Central didn't accept the app. " + ex.Message);
        }

        _panelCache.Invalidate(projectId, environmentId);
        _logger.LogInformation(
            "User {UserId} uploaded {FileName} ({Bytes} bytes) to {Environment} (project {ProjectId}); app {AppId} version {Version}, operation {OperationId}.",
            _orgContext.CurrentUserId, fileName, appBytes.Length, env.Name, projectId,
            operation.AppId, operation.TargetAppVersion, operation.Id);
        if (accepted is not null)
        {
            await accepted(operation, ct);
        }

        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            _apps, env.Token, env.Family, env.Name, operation, UploadPollDelay, UploadPollTimeout, ct, progress);
        _panelCache.Invalidate(projectId, environmentId);
        if (result.Raw is { } raw)
        {
            _logger.LogWarning("Business Central reported the install of {FileName} on {Environment} as failed: {Raw}", fileName, env.Name, raw);
        }
        return result;
    }

    /// <summary>
    /// Asks Business Central again how a booked install it had already accepted ended,
    /// from the app and operation ids stamped on the booking, and waits for it as the send
    /// would have. For an install the workbench did not see finish: a restart during the
    /// poll, or a poll that gave up as unconfirmed. Resolves the connection the way the
    /// send does, as the requester, so an access change since the booking is honoured;
    /// refusals come back as the send's would (a validation or access exception), and the
    /// caller (<see cref="UpgradeActionWorker"/>) decides what the row says.
    /// </summary>
    /// <param name="progress">Called on each poll, so a worker can keep its heartbeat alive through a long one.</param>
    internal async Task<BcAppOperationResult> ConfirmBookedInstallAsync(
        int projectId, int environmentId, Guid appId, Guid operationId, CancellationToken ct, Action? progress = null)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        // The poller reads only the two ids; the rest is filler for the record's shape.
        var started = new BcAppOperation(operationId, appId, string.Empty, BcAppOperationStatus.Unknown, string.Empty,
            string.Empty, string.Empty, null, string.Empty, string.Empty, string.Empty, false, string.Empty, null, null, null);
        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            _apps, env.Token, env.Family, env.Name, started, UploadPollDelay, UploadPollTimeout, ct, progress);
        _panelCache.Invalidate(projectId, environmentId);
        _logger.LogInformation(
            "Checked operation {OperationId} of app {AppId} on {Environment} (project {ProjectId}) again: completed {Completed}, unconfirmed {Unconfirmed}.",
            operationId, appId, env.Name, projectId, result.Completed, result.IsUnconfirmed);
        if (result.Raw is { } raw)
        {
            _logger.LogWarning("Business Central reported operation {OperationId} of app {AppId} on {Environment} as failed: {Raw}", operationId, appId, env.Name, raw);
        }
        return result;
    }

    /// <summary>How long one booked upload's install is waited for before it is given up as unconfirmed. The worker's budget is worked out from it.</summary>
    internal static readonly TimeSpan DefaultUploadPollTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often a booked upload's install is re-read, and how long it is waited for -
    /// the delivery path's numbers. Settable so a test need not wait five seconds a poll.
    /// </summary>
    internal TimeSpan UploadPollDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan UploadPollTimeout { get; set; } = DefaultUploadPollTimeout;

    /// <summary>
    /// A minute of slack on a picked slot, because "now" travels between the person's
    /// clock, the page and this method. The dialog refuses with the same allowance, so a
    /// slot the form accepts is one the service accepts.
    /// </summary>
    internal static readonly TimeSpan BookingPastSlack = TimeSpan.FromMinutes(1);

    /// <summary>The file's own name, whichever separator the person's machine used: a Windows path is not one to Linux.</summary>
    private static string CleanUploadName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        return name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    /// <summary>
    /// Puts a line in the environment's update history for something that was sent to
    /// the customer's tenant there and then. Written already <c>Sent</c>, so the worker
    /// that fires booked actions never sees it. The write to Business Central has
    /// happened by now and cannot be taken back, so a failure to record it is logged and
    /// swallowed: reporting the update as failed would be the bigger lie.
    /// </summary>
    private async Task RecordEnvironmentActionAsync(
        int projectId, int environmentId, UpgradeActionKind kind, string outcome, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            _db.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
            {
                OrganizationId = RequireOrganizationId(),
                ProjectId = projectId,
                EnvironmentId = environmentId,
                Kind = kind,
                Status = UpgradeActionStatus.Sent,
                RequestedByUserId = _orgContext.CurrentUserId,
                RequestedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
                RequestedAt = now,
                ExecuteAfter = now,
                SentAt = now,
                Outcome = outcome,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Couldn't record {Kind} in the update history of environment {EnvironmentId}.", kind, environmentId);
        }
    }

    /// <summary>
    /// Sets or clears an environment's recurring update window. Pass both
    /// <paramref name="start"/> and <paramref name="end"/> to set it, or both null to
    /// clear it ("any time"); passing only one is a validation error. Interpreted in the
    /// project's timezone. Access-gated; survives a Refresh (the discovery upsert only
    /// touches fetched fields). See <c>.design/saas-delivery.md</c> ("Update window").
    /// </summary>
    public async Task SetUpdateWindowAsync(int projectId, int environmentId, TimeOnly? start, TimeOnly? end, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(projectId, ownerId, ct);

        if (start is null != (end is null))
        {
            throw Validation("UpdateWindow", "Set both a start and an end time for the window, or clear both for 'any time'.");
        }

        var env = await _db.OeProjectEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct)
            ?? throw Validation("Environment", "That environment no longer exists.");

        env.UpdateWindowStart = start;
        env.UpdateWindowEnd = end;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Set update window {Start}-{End} for environment {EnvId} (project {ProjectId}).",
            start, end, environmentId, projectId);
    }

    /// <summary>
    /// What setting the delivery window to <paramref name="start"/>-<paramref name="end"/>
    /// on each of <paramref name="environmentIds"/> would do, one row per id in the order
    /// given (issue #961): <see cref="DeliveryWindowChangeGroup.WillChange"/>,
    /// <see cref="DeliveryWindowChangeGroup.AlreadySet"/>,
    /// <see cref="DeliveryWindowChangeGroup.NoAccess"/> or
    /// <see cref="DeliveryWindowChangeGroup.Missing"/>. Reads only our own mirror.
    /// <para>Whether the caller manages a solution is asked once per solution, not once
    /// per row. An environment of a solution the caller cannot see answers as one that
    /// does not exist, as <see cref="UpgradeFleetService.GetEnvironmentAsync"/> does.</para>
    /// </summary>
    public async Task<List<DeliveryWindowChangePreviewRow>> PreviewUpdateWindowForManyAsync(
        IReadOnlyCollection<int> environmentIds, TimeOnly? start, TimeOnly? end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environmentIds);
        RequireOrganizationId();
        if (start is null != (end is null))
        {
            throw Validation("UpdateWindow", "Set both a start and an end time for the window, or clear both for 'any time'.");
        }

        var ids = environmentIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var found = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Where(e => _db.OeProjects.Where(visible).Any(p => p.Id == e.ProjectId))
            .Select(e => new
            {
                e.Id,
                e.ProjectId,
                ProjectName = e.Project!.Name,
                ProjectDeleted = e.Project!.DeletedAt != null,
                OwnerId = e.Project!.CreatedByUserId,
                e.Name,
                e.Type,
                e.Status,
                e.SoftDeletedOn,
                e.MissingSince,
                e.UpdateWindowStart,
                e.UpdateWindowEnd,
            })
            .ToDictionaryAsync(e => e.Id, ct);

        // A deployment already booked for the environment's current window keeps its time;
        // the preview says so, because the new window then only applies from the next one.
        var waiting = (await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Status == ProjectDeliveryStatus.Scheduled && d.ScheduledByDeliveryWindow)
            .Where(d => ids.Contains(d.ReleasePipeline!.ProjectEnvironmentId))
            .Select(d => d.ReleasePipeline!.ProjectEnvironmentId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var canManage = new Dictionary<int, bool>();
        foreach (var project in found.Values.Where(e => !e.ProjectDeleted).GroupBy(e => e.ProjectId))
        {
            canManage[project.Key] = await _access.CanManageAsync(project.Key, project.First().OwnerId, ct);
        }

        var rows = new List<DeliveryWindowChangePreviewRow>(ids.Count);
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var e))
            {
                rows.Add(new DeliveryWindowChangePreviewRow(
                    id, null, null, null, null, null, null, DeliveryWindowChangeGroup.Missing, false));
                continue;
            }

            var gone = e.ProjectDeleted || e.MissingSince is not null
                || e.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(e.Status);
            var group = gone ? DeliveryWindowChangeGroup.Missing
                : !canManage[e.ProjectId] ? DeliveryWindowChangeGroup.NoAccess
                : e.UpdateWindowStart == start && e.UpdateWindowEnd == end ? DeliveryWindowChangeGroup.AlreadySet
                : DeliveryWindowChangeGroup.WillChange;
            rows.Add(new DeliveryWindowChangePreviewRow(
                id, e.ProjectId, e.ProjectName, e.Name, e.Type, e.UpdateWindowStart, e.UpdateWindowEnd,
                group, group == DeliveryWindowChangeGroup.WillChange && waiting.Contains(id)));
        }
        return rows;
    }

    /// <summary>
    /// Sets the delivery window on many environments at once (issue #961). Groups first
    /// with <see cref="PreviewUpdateWindowForManyAsync"/>, then writes each
    /// <see cref="DeliveryWindowChangeGroup.WillChange"/> row through
    /// <see cref="SetUpdateWindowAsync"/>, so the "both or neither" rule, the access check
    /// and the log line are the single-environment ones. Every other row is skipped. A
    /// row that fails is reported and does not stop the rest: the result has one entry
    /// per id, which is what the dialog shows afterwards.
    /// </summary>
    public async Task<List<DeliveryWindowChangeResult>> SetUpdateWindowForManyAsync(
        IReadOnlyCollection<int> environmentIds, TimeOnly? start, TimeOnly? end, CancellationToken ct = default)
    {
        var preview = await PreviewUpdateWindowForManyAsync(environmentIds, start, end, ct);
        var results = new List<DeliveryWindowChangeResult>(preview.Count);
        foreach (var row in preview)
        {
            if (row.Group != DeliveryWindowChangeGroup.WillChange)
            {
                results.Add(new DeliveryWindowChangeResult(row, DeliveryWindowChangeOutcome.Skipped, null));
                continue;
            }
            try
            {
                await SetUpdateWindowAsync(row.ProjectId!.Value, row.EnvironmentId, start, end, ct);
                results.Add(new DeliveryWindowChangeResult(row, DeliveryWindowChangeOutcome.Changed, null));
            }
            catch (PlanValidationException ex)
            {
                results.Add(new DeliveryWindowChangeResult(row, DeliveryWindowChangeOutcome.Failed,
                    ex.Errors.Values.FirstOrDefault() ?? "The window couldn't be saved."));
            }
            catch (ProjectAccessDeniedException ex)
            {
                results.Add(new DeliveryWindowChangeResult(row, DeliveryWindowChangeOutcome.Failed, ex.Message));
            }
        }

        _logger.LogInformation(
            "User {UserId} set the delivery window {Start}-{End} on {Changed} of {Selected} environment(s) ({Failed} failed).",
            _orgContext.CurrentUserId, start, end,
            results.Count(r => r.Outcome == DeliveryWindowChangeOutcome.Changed), results.Count,
            results.Count(r => r.Outcome == DeliveryWindowChangeOutcome.Failed));
        return results;
    }

    /// <summary>
    /// Resolves a project's BC credentials and returns the token plus tenant the
    /// delivery worker publishes with. Deliberately <strong>not</strong> access-gated:
    /// it's called from the delivery worker <em>after</em> the release was authorised at
    /// creation, under the triggering user's captured identity (so the org query filter
    /// still scopes the project). The secret never leaves this service. Throws
    /// <see cref="BcApiException"/> with a clear, secret-free message when the connection
    /// isn't configured, the key ring can't decrypt the secret, the secret has expired,
    /// or Entra rejects the credentials — the worker records that as the failure reason.
    /// See <c>.design/saas-delivery.md</c> ("Authentication", "Expired-secret behaviour").
    /// </summary>
    public async Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw new BcApiException(null, "This project no longer exists.");

        var creds = await ResolveCredentialsAsync(project, ct)
            ?? throw new BcApiException(null,
                "The Business Central connection isn't set up (or its secret can't be decrypted). Re-enter it on the solution's Business Central page.");

        if (creds.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow)
        {
            // Never a quiet switch to the other registration: which one a customer has
            // authorised is theirs to know, and a fallback would hide it.
            throw new BcApiException(null, creds.FromOrganization
                ? "Your organisation's Business Central client secret has expired. An administrator has to rotate it in Entra and re-enter it under Administration before deploying."
                : "This solution's own Business Central client secret has expired. Rotate it in Entra and re-enter it on the solution's Business Central tab, or switch the solution to your organisation's app registration there.");
        }

        var token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct)
            .ConfigureAwait(false);
        return new BcDeliveryContext(token, creds.TenantId);
    }

    /// <summary>
    /// Stable upsert of the fetched environments onto the project's tracked
    /// <see cref="OeProject.Environments"/>: match by name (preserving each row's id and
    /// picked company), add new ones, and stamp <c>MissingSince</c> on any that the
    /// fetch no longer returns rather than deleting them — so a release pipeline's FK
    /// never dangles. Assumes the caller saves.
    /// <para>
    /// One wrinkle makes "match by name" not quite enough: when a customer soft-deletes
    /// an environment, Business Central hands the name back under a <em>new</em> one with
    /// the deletion time appended (<c>JLE</c> returns as <c>JLE-260911110359</c>), so the
    /// original is free to be reused. Read literally that is one environment vanishing
    /// and a stranger appearing, which is what issue #808 saw on screen. The fold below
    /// puts the pair back onto the row the pipelines already point at. See
    /// <c>.design/saas-delivery.md</c> ("soft_deleted_on and missing_since").
    /// </para>
    /// </summary>
    private async Task UpsertEnvironmentsAsync(OeProject project, IReadOnlyList<BcEnvironment> fetched, CancellationToken ct)
    {
        var existing = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == project.Id)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedNames = fetched.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DefaultDeliveryWindows? defaults = null; // read at most once, and only if a row is born

        foreach (var env in fetched)
        {
            seen.Add(env.Name);
            if (byName.TryGetValue(env.Name, out var row))
            {
                ApplyFetched(row, env, now);
                row.MissingSince = null; // back if it had vanished

                // The fold below only happens the first time the renamed environment is
                // seen. A solution that met it before the fold existed has both rows
                // already - the old name, "no longer present" for good, and the stamped
                // one - so a refresh that finds the pair puts them back together.
                if (FoldTarget(env, byName, fetchedNames) is { } stale && stale.Id != row.Id)
                {
                    await AbsorbStaleTwinAsync(row, stale, ct);
                    byName.Remove(stale.Name);
                    existing.Remove(stale);
                    _logger.LogInformation(
                        "Merged environment row {StaleEnvironmentName} into its soft-deleted continuation {EnvironmentName} for project {ProjectId}.",
                        stale.Name, row.Name, project.Id);
                }
                continue;
            }

            if (FoldTarget(env, byName, fetchedNames) is { } renamed)
            {
                _logger.LogInformation(
                    "Business Central renamed soft-deleted environment {OldEnvironmentName} to {NewEnvironmentName} for project {ProjectId}; folded onto the existing row.",
                    renamed.Name, env.Name, project.Id);
                seen.Add(renamed.Name); // so the pass below doesn't call the old name missing
                renamed.Name = env.Name; // the API name is what later admin-center calls address
                byName[env.Name] = renamed;
                ApplyFetched(renamed, env, now);
                renamed.MissingSince = null;
                continue;
            }

            // Nothing to fold onto — including the reverse race, where the first refresh
            // after the deletion is also the first time we hear of the environment at
            // all. Then the suffixed name is simply a new environment, soft-deleted from
            // the moment we meet it, and that is the honest thing to show.
            var row2 = new OeProjectEnvironment
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Name = env.Name,
            };
            ApplyFetched(row2, env, now);
            // The one place a row is born, so the one place the organisation's default
            // window for its type applies (issue #962). Copied as clock digits: both the
            // default and the row's window are read in the customer's zone.
            defaults ??= await LoadDefaultDeliveryWindowsAsync(project.OrganizationId, ct);
            (row2.UpdateWindowStart, row2.UpdateWindowEnd) = DefaultWindowFor(defaults, env.Type);
            _db.OeProjectEnvironments.Add(row2);
        }

        foreach (var row in existing)
        {
            if (!seen.Contains(row.Name) && row.MissingSince is null)
            {
                row.MissingSince = now;
            }
        }
    }

    /// <summary>
    /// Merges the row an environment had under its old name into the row it has under its
    /// soft-deleted one. The stamped row survives, because its name is the one the API
    /// answers to and keeping it means nothing is renamed into a unique index mid-save.
    /// What the old row carried comes across first: its release pipelines (which would
    /// otherwise block the delete), its update history, and the delivery window somebody
    /// set on it if the survivor has none. The caller saves.
    /// </summary>
    private async Task AbsorbStaleTwinAsync(OeProjectEnvironment survivor, OeProjectEnvironment stale, CancellationToken ct)
    {
        var pipelines = await _db.OeReleasePipelines
            .Where(r => r.ProjectEnvironmentId == stale.Id)
            .ToListAsync(ct);
        foreach (var pipeline in pipelines) pipeline.ProjectEnvironmentId = survivor.Id;

        // A set-based update: a pending upload booking carries its package, and there is
        // no reason to pull fifty megabytes into memory to change one foreign key.
        await _db.OeEnvironmentUpgradeActions
            .Where(a => a.EnvironmentId == stale.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EnvironmentId, survivor.Id), ct);

        // Its places on planned upgrades (#984) come across too, or the delete below would
        // cascade them away. A place the survivor already holds - on the same upgrade, or
        // on an open one when this one is open as well - is dropped rather than moved, since
        // the unique indexes allow the environment one of each.
        var lines = await _db.OeEnvironmentUpgradeLines
            .Where(l => l.EnvironmentId == stale.Id || l.EnvironmentId == survivor.Id)
            .ToListAsync(ct);
        var held = lines.Where(l => l.EnvironmentId == survivor.Id).ToList();
        foreach (var line in lines.Where(l => l.EnvironmentId == stale.Id))
        {
            if (held.Any(h => h.UpgradeId == line.UpgradeId || (h.IsOpen && line.IsOpen)))
            {
                _db.OeEnvironmentUpgradeLines.Remove(line);
                continue;
            }
            line.EnvironmentId = survivor.Id;
            held.Add(line);
        }

        if (survivor.UpdateWindowStart is null && survivor.UpdateWindowEnd is null)
        {
            survivor.UpdateWindowStart = stale.UpdateWindowStart;
            survivor.UpdateWindowEnd = stale.UpdateWindowEnd;
        }

        _db.OeProjectEnvironments.Remove(stale);
    }

    /// <summary>
    /// The existing row a freshly-seen, soft-deleted environment is the renamed
    /// continuation of, or null when there is none and it should be inserted as new.
    /// Deliberately narrow: only a soft-deleted fetch with a deletion stamp on its name
    /// folds, only onto a row that isn't itself soft-deleted, and only when the base name
    /// is absent from this same fetch — a customer who has already created a fresh
    /// <c>JLE</c> alongside the deleted one must keep two rows.
    /// </summary>
    private static OeProjectEnvironment? FoldTarget(
        BcEnvironment env,
        Dictionary<string, OeProjectEnvironment> byName,
        HashSet<string> fetchedNames)
    {
        if (!IsSoftDeleted(env)) return null;
        if (SoftDeleteStampedBaseName(env.Name) is not { } baseName) return null;
        if (fetchedNames.Contains(baseName)) return null;
        if (!byName.TryGetValue(baseName, out var row)) return null;
        return row.SoftDeletedOn is null ? row : null;
    }

    /// <summary>
    /// True when the API says this environment has been soft-deleted. Either signal is
    /// enough: the stamp can arrive without the status and the other way round.
    /// </summary>
    private static bool IsSoftDeleted(BcEnvironment env) =>
        env.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(env.Status);

    /// <summary>
    /// Strips the <c>-yyMMddHHmmss</c> deletion stamp Business Central appends when an
    /// environment is soft-deleted, returning the name it had before
    /// (<c>JLE-260911110359</c> → <c>JLE</c>), or null when the name carries no such
    /// stamp. Twelve digits are all this checks: whether they parse as a plausible date
    /// is Microsoft's business, and the caller only folds a name that the API has also
    /// told us is soft-deleted.
    /// </summary>
    internal static string? SoftDeleteStampedBaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || name.Length - dash - 1 != 12) return null;
        for (var i = dash + 1; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) return null;
        }
        return name[..dash];
    }

    /// <summary>
    /// Mirrors each environment's <em>Microsoft</em> update window and its next platform
    /// update onto its row.
    /// <para>
    /// This is two extra calls per environment on top of the single list call — twenty
    /// sandboxes make a Refresh forty-one requests instead of one. It rides the Refresh
    /// anyway because the alternative (fetching when a panel opens) would put a network
    /// round trip in the way of every glance at the table, and the window changes about
    /// as often as the environment list does. If it ever bites, this is the method to
    /// make lazy; the stamped <c>BcUpdateWindowFetchedAt</c> already lets the UI say how
    /// old the answer is.
    /// </para>
    /// <para>
    /// A failure for one environment must not fail the Refresh: the environment list is
    /// the point of the operation, and this is context beside it. On failure the previous
    /// answer and its age are left alone rather than blanked, so the table degrades to
    /// stale rather than to empty. The two mirrors fail independently — a denied updates
    /// read still leaves a freshly-read window.
    /// </para>
    /// </summary>
    private async Task MirrorBcEnvironmentDetailsAsync(OeProject project, string token, CancellationToken ct)
    {
        var rows = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == project.Id && e.MissingSince == null)
            .ToListAsync(ct);

        // Storage is the tenant's: one read covers every environment's size and the one
        // allowance they share. A failure costs the freshness, never the figures.
        try
        {
            var storage = await _adminClient.GetTenantStorageAsync(token, ct);
            foreach (var row in rows)
            {
                row.BcDatabaseKb = storage.DatabaseKilobytesByEnvironment.TryGetValue(row.Name, out var kb) ? kb : null;
            }
            project.BcStorageQuotaKb = storage.AllowedKilobytes;
            project.BcStorageFetchedAt = _clock.GetUtcNow().UtcDateTime;
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "Couldn't read the storage figures for project {ProjectId}: {Message}.", project.Id, ex.Message);
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var settings = await _adminClient.GetUpdateSettingsAsync(token, row.ApplicationFamily, row.Name, ct);
                row.BcUpdateWindowStart = settings?.StartTime;
                row.BcUpdateWindowEnd = settings?.EndTime;
                row.BcUpdateWindowTimeZoneId = settings?.WindowsTimeZoneId;
                row.BcUpdateWindowTimeZoneIana = BcUpdateWindow.ToIana(settings?.WindowsTimeZoneId);
                row.BcUpdateWindowFetchedAt = DateTime.UtcNow;
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the Business Central update window for {Environment} (project {ProjectId}): {Message}.",
                    row.Name, project.Id, ex.Message);
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                var updates = await _adminClient.ListEnvironmentUpdatesAsync(token, row.ApplicationFamily, row.Name, ct);
                ApplyReadNextUpdate(row, updates);
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the Business Central platform updates for {Environment} (project {ProjectId}): {Message}.",
                    row.Name, project.Id, ex.Message);
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                var family = string.IsNullOrWhiteSpace(row.ApplicationFamily) ? BcConstants.DefaultApplicationFamily : row.ApplicationFamily;
                var apps = await _apps.ListInstalledAppsAsync(token, family, row.Name, ct);
                if (apps.Count > 0) await MirrorInstalledAppsAsync(project.OrganizationId, row.Id, apps, ct);
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the installed apps for {Environment} (project {ProjectId}): {Message}.",
                    row.Name, project.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// The one update out of an environment's list worth caching: the <em>selected</em>
    /// one when the customer has picked a slot (that is the answer to "when does this
    /// customer move?"), else the newest one they could still pick, else nothing. An
    /// unavailable, unselected version is a Microsoft roadmap entry with no date on it,
    /// so it is not a candidate.
    /// </summary>
    internal static BcEnvironmentUpdate? PickNextUpdate(IReadOnlyList<BcEnvironmentUpdate> updates)
    {
        var selected = updates.FirstOrDefault(u => u.Selected);
        if (selected is not null) return selected;

        BcEnvironmentUpdate? newest = null;
        foreach (var candidate in updates)
        {
            if (!candidate.Available) continue;
            if (newest is null || CompareVersions(candidate.TargetVersion, newest.TargetVersion) > 0)
            {
                newest = candidate;
            }
        }
        return newest;
    }

    /// <summary>
    /// Orders two BC platform versions by numeric segment, because a string compare puts
    /// "10.1" before "9.2" and would quietly pick last year's update as the newest.
    /// A segment that isn't a number sorts as 0 rather than throwing — Microsoft's
    /// version strings are theirs to change.
    /// </summary>
    internal static int CompareVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var xv) ? xv : 0;
            var y = i < b.Length && int.TryParse(b[i], out var yv) ? yv : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>
    /// Writes the picked update onto the row, clearing all six value columns when there
    /// is nothing to show, and the list of versions on offer beside it. Either way
    /// <c>BcNextUpdateFetchedAt</c> is stamped: an empty list is a successful read that
    /// says "nothing is scheduled", which is a different fact from "we never asked".
    /// </summary>
    private static void ApplyNextUpdate(OeProjectEnvironment row, IReadOnlyList<BcEnvironmentUpdate> updates)
    {
        var update = PickNextUpdate(updates);
        row.BcNextUpdateVersion = update?.TargetVersion;
        row.BcNextUpdateType = update?.TargetVersionType;
        row.BcNextUpdateStatus = update?.UpdateStatus;
        row.BcNextUpdateDate = update?.SelectedDateTime?.UtcDateTime;
        row.BcNextUpdateLatestDate = update?.LatestSelectableDateTime?.UtcDateTime;
        row.BcNextUpdateIgnoresWindow = update is null ? null : update.IgnoreUpdateWindow;
        row.BcOfferedVersions = OfferedVersions(updates);
        row.BcNextUpdateFetchedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The versions an environment could be set to: every <c>available</c> one, distinct,
    /// newest first by numeric segment. An unreleased version has no date and cannot be
    /// picked, so it is left out for the same reason <see cref="PickNextUpdate"/> skips it.
    /// </summary>
    internal static List<string> OfferedVersions(IReadOnlyList<BcEnvironmentUpdate> updates) =>
        updates
            .Where(u => u.Available && !string.IsNullOrWhiteSpace(u.TargetVersion))
            .Select(u => u.TargetVersion.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .ToList();

    /// <summary>
    /// Copies the fetched detail from one API record onto a row. Only fields the API
    /// reports are touched, so the user's own settings on the row (the delivery window)
    /// survive a refresh. <c>geoName</c> is absent from the by-name
    /// response, so a null there leaves the cached value in place rather than erasing it.
    /// </summary>
    private static void ApplyFetched(OeProjectEnvironment row, BcEnvironment env, DateTime now)
    {
        row.Type = env.Type;
        row.FriendlyName = env.FriendlyName;
        row.ApplicationFamily = env.ApplicationFamily;
        row.Status = env.Status;
        row.StatusFetchedAt = now;
        row.CountryCode = env.CountryCode;
        row.AadTenantId = env.AadTenantId;
        row.WebClientLoginUrl = env.WebClientLoginUrl;
        row.LocationName = env.LocationName;
        row.GeoName = env.GeoName ?? row.GeoName;
        row.RingName = env.RingName;
        row.AppSourceAppsUpdateCadence = env.AppSourceAppsUpdateCadence;
        row.Version = env.Version;
        row.GracePeriodStartDate = env.GracePeriodStartDate;
        row.EnforcedUpdatePeriodStartDate = env.EnforcedUpdatePeriodStartDate;
        row.SoftDeletedOn = env.SoftDeletedOn;
        row.HardDeletePendingOn = env.HardDeletePendingOn;
        row.DeleteReason = env.DeleteReason;
        row.FetchedAt = now;
    }

    /// <summary>
    /// The organisation's default delivery windows (issue #962). Runs under the query
    /// filter: a request has its org in scope, and the discovery worker pins the
    /// project's org before it refreshes, so the predicate and the filter agree.
    /// </summary>
    private async Task<DefaultDeliveryWindows> LoadDefaultDeliveryWindowsAsync(int organizationId, CancellationToken ct) =>
        await _db.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => new DefaultDeliveryWindows(
                s.DefaultDeliveryWindowProductionStart, s.DefaultDeliveryWindowProductionEnd,
                s.DefaultDeliveryWindowSandboxStart, s.DefaultDeliveryWindowSandboxEnd))
            .FirstOrDefaultAsync(ct)
        ?? DefaultDeliveryWindows.None;

    /// <summary>The default pair for an environment type; anything that is neither Production nor Sandbox gets no window.</summary>
    private static (TimeOnly? Start, TimeOnly? End) DefaultWindowFor(DefaultDeliveryWindows defaults, string? type)
    {
        if (string.Equals(type, BcEnvironmentTypes.Production, StringComparison.OrdinalIgnoreCase))
        {
            return (defaults.ProductionStart, defaults.ProductionEnd);
        }
        if (string.Equals(type, BcEnvironmentTypes.Sandbox, StringComparison.OrdinalIgnoreCase))
        {
            return (defaults.SandboxStart, defaults.SandboxEnd);
        }
        return (null, null);
    }

    /// <summary>Decrypts the stored credentials, or null when not fully configured / the key ring can't decrypt the secret.</summary>
    /// <summary>The credentials a solution connects with, and where they came from. Never leaves this service.</summary>
    private sealed record ResolvedCredentials(Guid TenantId, string ClientId, string Secret, DateTime? ExpiresAt, bool FromOrganization);

    /// <summary>
    /// A solution connects with its own app registration when it has one, and otherwise
    /// with the organisation's. The choice is the solution's client id: set means "this
    /// customer has a registration of their own", and then the organisation's is never
    /// tried - not even when the solution's own secret is missing or has expired. A
    /// fallback there would connect a customer through a registration nobody chose for
    /// them, so it fails and says so instead.
    /// </summary>
    private async Task<ResolvedCredentials?> ResolveCredentialsAsync(OeProject project, CancellationToken ct)
    {
        if (project.BcTenantId is null || project.BcTenantId == Guid.Empty) return null;

        string clientId;
        string? encrypted;
        DateTime? expiresAt;
        var fromOrganization = string.IsNullOrEmpty(project.BcClientId);
        if (fromOrganization)
        {
            // Scoped by the query filter as well; the predicate names the solution's own
            // organisation so the read is pinned whichever context it runs under.
            var shared = await _db.OrganizationSettings.AsNoTracking()
                .Where(o => o.OrganizationId == project.OrganizationId)
                .Select(o => new { o.BcClientId, o.BcClientSecretEncrypted, o.BcClientSecretExpiresAt })
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(shared?.BcClientId)) return null;
            (clientId, encrypted, expiresAt) = (shared.BcClientId, shared.BcClientSecretEncrypted, shared.BcClientSecretExpiresAt);
        }
        else
        {
            (clientId, encrypted, expiresAt) = (project.BcClientId!, project.BcClientSecretEncrypted, project.BcClientSecretExpiresAt);
        }
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return new ResolvedCredentials(
                project.BcTenantId.Value, clientId, _secretProtector.Unprotect(encrypted), expiresAt, fromOrganization);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex,
                "Could not decrypt the BC client secret for project {ProjectId} (organisation's registration: {FromOrganization}); it must be re-entered.",
                project.Id, fromOrganization);
            return null;
        }
    }

    private static bool IsValidTimeZone(string ianaId)
    {
        try
        {
            // .NET on Linux resolves IANA ids natively; on Windows it falls back via ICU.
            TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>The organisation's default app registration as the Administration page shows it. Never carries the secret.</summary>
public sealed record OrganizationBcRegistration(
    string? ClientId,
    bool HasSecret,
    DateTime? SecretExpiresAt,
    int SolutionsUsingIt,
    int SolutionsWithTheirOwn,
    DefaultDeliveryWindows DefaultWindows)
{
    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && HasSecret;
}

/// <summary>Form-post shape for the organisation's app registration. The secret is keep-on-blank.</summary>
public sealed record OrganizationBcRegistrationInput(string? ClientId, string? ClientSecret, DateTime? SecretExpiresAt);

/// <summary>Form-post shape for a project's BC connection. The secret is keep-on-blank (empty leaves the stored one).</summary>
public sealed record BcConnectionInput(
    Guid? TenantId,
    string? ClientId,
    string? ClientSecret,
    DateTime? SecretExpiresAt,
    string? TimeZone,
    /// <summary>True to connect with the organisation's app registration; the solution's own client id, secret and expiry are then cleared.</summary>
    bool UseOrganizationRegistration = false);

/// <summary>Presence/verification view of a project's BC connection. Never carries the secret.</summary>
public sealed record BcConnectionStatus(
    bool IsConfigured,
    Guid? TenantId,
    string? ClientId,
    bool HasSecret,
    DateTime? SecretExpiresAt,
    DateTime? CredentialsUpdatedAt,
    string? TimeZone,
    DateTime? VerifiedAt,
    /// <summary>True when the solution has no registration of its own and the organisation's is what it connects with.</summary>
    bool UsesOrganizationRegistration = false,
    /// <summary>True when the organisation has a complete app registration a solution could use.</summary>
    bool OrganizationRegistrationAvailable = false,
    /// <summary>When the secret actually in use expires - the solution's own, or the organisation's.</summary>
    DateTime? EffectiveSecretExpiresAt = null,
    /// <summary>The organisation's client id, when it has a complete registration - what a customer authorises in their admin centre.</summary>
    string? OrganizationClientId = null);

/// <summary>One file handed to <see cref="ProjectConnectionService.InstallUploadedAppsAsync"/>.</summary>
public sealed record UploadPackage(byte[] Bytes, string FileName);

/// <summary>
/// What <see cref="ProjectConnectionService.InstallUploadedAppsAsync"/> booked.
/// <paramref name="Timing"/> is the timing that <em>applied</em>, which can differ from
/// the one asked for: the delivery window falls back to Microsoft's window when the
/// environment has none. <paramref name="RunsAtUtc"/> is when the worker will start the
/// batch - already past for "now".
/// </summary>
public sealed record UploadAppOutcome(UploadAppTiming Timing, DateTime RunsAtUtc, int Count);

/// <summary>Where one environment lands in the preview of "set the delivery window" (issue #961).</summary>
public enum DeliveryWindowChangeGroup
{
    /// <summary>The window differs from the one asked for, and the caller manages the solution.</summary>
    WillChange,

    /// <summary>The environment already has exactly this window; skipped.</summary>
    AlreadySet,

    /// <summary>The caller cannot manage the environment's solution; skipped.</summary>
    NoAccess,

    /// <summary>The environment, or its solution, no longer exists, or the customer deleted it; skipped.</summary>
    Missing,
}

/// <summary>
/// One environment in the preview of a bulk delivery-window change. The names and the
/// current window are null for a <see cref="DeliveryWindowChangeGroup.Missing"/> row this
/// caller could not read at all.
/// </summary>
/// <param name="HasDeploymentWaitingForWindow">
/// True on a row that will change and already has a deployment scheduled for its current
/// delivery window. That deployment keeps its time; the next one uses the new window.
/// </param>
public sealed record DeliveryWindowChangePreviewRow(
    int EnvironmentId,
    int? ProjectId,
    string? ProjectName,
    string? EnvironmentName,
    string? EnvironmentType,
    TimeOnly? CurrentStart,
    TimeOnly? CurrentEnd,
    DeliveryWindowChangeGroup Group,
    bool HasDeploymentWaitingForWindow)
{
    /// <summary>True for a Production environment.</summary>
    public bool IsProduction => string.Equals(EnvironmentType, "Production", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What happened to one environment in a bulk delivery-window change.</summary>
public enum DeliveryWindowChangeOutcome { Changed, Skipped, Failed }

/// <summary>One environment's result of a bulk delivery-window change; <paramref name="Error"/> is set on a failed row.</summary>
public sealed record DeliveryWindowChangeResult(
    DeliveryWindowChangePreviewRow Row,
    DeliveryWindowChangeOutcome Outcome,
    string? Error);

/// <summary>One fetched BC environment — the project detail page's environment row.</summary>
public sealed record ProjectEnvironmentRow(
    int Id,
    string Name,
    string Type,
    DateTime FetchedAt,
    DateTime? MissingSince,
    TimeOnly? UpdateWindowStart,
    TimeOnly? UpdateWindowEnd,
    /// <summary>Lifecycle status from the last fetch, verbatim. Null on rows fetched before it was captured.</summary>
    string? Status,
    /// <summary>How often Marketplace apps update on the environment (a <see cref="BcAppUpdateCadence"/> value).</summary>
    string? AppSourceAppsUpdateCadence,
    /// <summary>Start of Microsoft's platform-update window, in <see cref="BcUpdateWindowTimeZoneIana"/>. Not the delivery window.</summary>
    TimeOnly? BcUpdateWindowStart,
    /// <summary>End of Microsoft's platform-update window.</summary>
    TimeOnly? BcUpdateWindowEnd,
    /// <summary>IANA form of the zone Microsoft's window is expressed in; null when the Windows id had no mapping.</summary>
    string? BcUpdateWindowTimeZoneIana,
    /// <summary>When the Microsoft window was last read successfully.</summary>
    DateTime? BcUpdateWindowFetchedAt,
    /// <summary>The environment's Business Central version from the last fetch.</summary>
    string? Version,
    /// <summary>Deep link into the environment's web client, for "Open in Business Central".</summary>
    string? WebClientLoginUrl);

/// <summary>
/// A live snapshot of one Business Central environment, for the panel on the project's
/// Business Central tab. Nothing here is persisted — it answers "what is on this
/// environment and what is about to change" at the moment the panel was opened.
/// Each section carries its own error so one refusal doesn't blank the rest.
/// </summary>
public sealed record BcEnvironmentPanel(
    string EnvironmentName,
    /// <summary>App ids this workbench has released to this environment, for highlighting our own extensions.</summary>
    IReadOnlySet<Guid> ReleasedAppIds,
    IReadOnlyList<BcInstalledApp> InstalledApps,
    string? InstalledAppsError,
    IReadOnlyList<BcAvailableAppUpdate> AvailableUpdates,
    string? AvailableUpdatesError,
    IReadOnlyList<BcScheduledPteOperation> ScheduledInstalls,
    string? ScheduledInstallsError,
    IReadOnlyList<BcEnvironmentUpdate> EnvironmentUpdates,
    string? EnvironmentUpdatesError,
    /// <summary>When these sections were read from Business Central — a cached panel keeps its original read time, so the page can say how old the answer is.</summary>
    DateTime FetchedAtUtc);

/// <summary>
/// What one environment's row says after <see cref="ProjectConnectionService.RefreshEnvironmentAsync"/>
/// re-read it: the fields the Upgrades page shows, exactly as the mirror now holds them.
/// Returned rather than re-read from the fleet, so a watch tick costs the page no query
/// of its own.
/// </summary>
public sealed record BcEnvironmentReading(
    string? Status,
    string? Version,
    DateTime? EnvironmentFetchedAt,
    string? NextUpdateVersion,
    string? NextUpdateType,
    string? NextUpdateStatus,
    DateTime? NextUpdateDate,
    DateTime? NextUpdateLatestDate,
    bool? NextUpdateIgnoresWindow,
    List<string>? OfferedVersions,
    DateTime? NextUpdateFetchedAt)
{
    /// <summary>
    /// <paramref name="row"/> with this reading laid over it. Everything the reading does
    /// not carry - who may act on the row, its solution, its storage - is kept.
    /// </summary>
    public UpgradeFleetRow ApplyTo(UpgradeFleetRow row) => row with
    {
        Status = Status,
        Version = Version,
        EnvironmentFetchedAt = EnvironmentFetchedAt,
        NextUpdateVersion = NextUpdateVersion,
        NextUpdateType = NextUpdateType,
        NextUpdateStatus = NextUpdateStatus,
        NextUpdateDate = NextUpdateDate,
        NextUpdateLatestDate = NextUpdateLatestDate,
        NextUpdateIgnoresWindow = NextUpdateIgnoresWindow,
        OfferedVersions = OfferedVersions,
        FetchedAt = NextUpdateFetchedAt,
    };
}
