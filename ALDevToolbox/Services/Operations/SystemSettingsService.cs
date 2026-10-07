using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

using ALDevToolbox.Services.Configuration;

namespace ALDevToolbox.Services.Operations;

/// <summary>
/// Read-side view of <see cref="SystemSettings"/> for the SiteAdmin form.
/// Carries <see cref="HasSmtpPassword"/> rather than the password itself —
/// plaintext is only ever materialised inside <see cref="ResolvedSmtpSettings"/>
/// for the email sender.
/// </summary>
public sealed record SystemSettingsView(
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    bool HasSmtpPassword,
    string? SmtpFrom,
    string? SmtpFromName,
    bool? SmtpUseStartTls,
    string? BannerText,
    bool BackupScheduleEnabled,
    TimeOnly BackupScheduleTimeUtc,
    int BackupRetentionCount,
    int PerTenantBackupRetentionCount,
    int? DefaultStorageQuotaMb,
    decimal IndexSizeMultiplier,
    bool McpEnabled,
    string? SignupEmailDomainAllowlist,
    string? ReleaseDownloadDomainAllowlist,
    IReadOnlyList<string> DisabledTools,
    int? BuildConcurrency,
    DateTime UpdatedAt);

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveAsync"/>.
/// <see cref="SmtpPassword"/> replaces the stored password when non-empty;
/// a blank value leaves it untouched (the form posts blank when the
/// SiteAdmin doesn't re-type the password). To clear an existing password,
/// set <see cref="ClearSmtpPassword"/> instead.
/// </summary>
public sealed record SystemSettingsInput(
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    string? SmtpPassword,
    bool ClearSmtpPassword,
    string? SmtpFrom,
    string? SmtpFromName,
    bool? SmtpUseStartTls,
    string? BannerText,
    bool BackupScheduleEnabled,
    TimeOnly BackupScheduleTimeUtc,
    int BackupRetentionCount,
    int PerTenantBackupRetentionCount,
    int? DefaultStorageQuotaMb,
    decimal IndexSizeMultiplier,
    bool McpEnabled,
    string? SignupEmailDomainAllowlist,
    string? ReleaseDownloadDomainAllowlist,
    IReadOnlyList<ALDevToolbox.Domain.Tools.ToolKey> DisabledTools,
    int? BuildConcurrency);

/// <summary>
/// SiteAdmin-facing view of the off-site backup settings. Carries flags
/// for whether keys are stored rather than the keys themselves; plaintext
/// only ever materialises in <see cref="ResolvedOffsiteSettings"/>.
/// </summary>
public sealed record OffsiteSettingsView(
    bool Enabled,
    string Provider,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    bool HasAccessKey,
    bool HasSecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveOffsiteAsync"/>. Empty
/// access/secret values leave the stored value untouched (same pattern as
/// SMTP password); set the explicit "Clear" flags to wipe them.
/// </summary>
public sealed record OffsiteSettingsInput(
    bool Enabled,
    string? Provider,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    string? AccessKey,
    bool ClearAccessKey,
    string? SecretKey,
    bool ClearSecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// Fully resolved off-site configuration with plaintext credentials.
/// Held only inside <see cref="OffsiteBackupService"/>; never persisted,
/// never logged.
/// </summary>
public sealed record ResolvedOffsiteSettings(
    string Provider,
    string? Endpoint,
    string? Region,
    string Bucket,
    string? Prefix,
    string AccessKey,
    string SecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// SiteAdmin-facing view of the deployment-wide Entra app registration used
/// for Microsoft sign-in. Carries a flag for whether a client secret is
/// stored rather than the secret itself.
/// </summary>
public sealed record EntraAppView(
    string? ClientId,
    bool HasClientSecret);

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveEntraAppAsync"/>. An empty
/// <see cref="ClientSecret"/> leaves the stored secret untouched (same
/// pattern as the SMTP password); set <see cref="ClearClientSecret"/> to wipe
/// it. Clearing the client id clears the paired secret with it.
/// </summary>
public sealed record EntraAppInput(
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret);

/// <summary>
/// SiteAdmin-facing view of the deployment-wide GitHub App registration.
/// Carries flags for whether the two secrets are stored rather than the
/// secrets themselves; plaintext only ever materialises inside
/// <see cref="ResolvedGitHubApp"/>.
/// </summary>
public sealed record GitHubAppView(
    long? AppId,
    string? AppSlug,
    string? ClientId,
    bool HasClientSecret,
    bool HasPrivateKey,
    bool HasWebhookSecret = false)
{
    /// <summary>
    /// True when an organisation could actually start the install handshake:
    /// the id and key are needed to mint tokens, the slug to build the install
    /// URL. The OAuth client id/secret belong to the per-user link (#621) and
    /// are deliberately not part of this test.
    /// </summary>
    public bool IsConfigured => AppId is not null && !string.IsNullOrEmpty(AppSlug) && HasPrivateKey;
}

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveGitHubAppAsync"/>. Empty
/// <see cref="ClientSecret"/> / <see cref="PrivateKeyPem"/> leave the stored
/// values untouched (same pattern as the SMTP password); the paired Clear flags
/// wipe them. Clearing the app id clears everything GitHub with it — the rest
/// is meaningless without the App it belongs to.
/// </summary>
public sealed record GitHubAppInput(
    string? AppId,
    string? AppSlug,
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret,
    string? PrivateKeyPem,
    bool ClearPrivateKey,
    string? WebhookSecret = null,
    bool ClearWebhookSecret = false);

/// <summary>
/// Fully resolved GitHub App credentials with plaintext secrets. Held only
/// inside <c>Services/GitHub/</c>; never persisted, never logged.
/// </summary>
public sealed record ResolvedGitHubApp(
    long AppId,
    string? AppSlug,
    string? ClientId,
    string? ClientSecret,
    string PrivateKeyPem);

/// <summary>
/// Resolved SMTP configuration. Either fully populated (host + from set) or
/// considered unconfigured. The plaintext password is only ever held in this
/// record — never persisted, never logged.
/// </summary>
public sealed record ResolvedSmtpSettings(
    string Host,
    int Port,
    string? User,
    string? Password,
    string From,
    string? FromName,
    bool UseStartTls)
{
    public static ResolvedSmtpSettings? TryFrom(string? host, int? port, string? user, string? password, string? from, string? fromName, bool? useStartTls)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) return null;
        return new ResolvedSmtpSettings(
            Host: host!,
            Port: port ?? 587,
            User: string.IsNullOrEmpty(user) ? null : user,
            Password: string.IsNullOrEmpty(password) ? null : password,
            From: from!,
            FromName: string.IsNullOrWhiteSpace(fromName) ? null : fromName!.Trim(),
            UseStartTls: useStartTls ?? true);
    }
}

/// <summary>
/// Reads and writes the singleton <see cref="SystemSettings"/> row. The
/// SMTP password is encrypted via ASP.NET Core Data Protection; decryption
/// is contained here, so callers see either a plaintext-bearing
/// <see cref="ResolvedSmtpSettings"/> (for the email sender) or a
/// <see cref="SystemSettingsView"/> (for the SiteAdmin form).
///
/// <para>
/// <see cref="ResolveSmtpAsync"/> prefers DB values and falls back to
/// <c>SMTP_*</c> env vars — fresh deployments can fire signup-approval
/// emails before any SiteAdmin has logged in to fill the form.
/// </para>
/// </summary>
public sealed class SystemSettingsService
{
    /// <summary>Data Protection purpose string for SMTP passwords.</summary>
    public const string SmtpPasswordProtectionPurpose = "ALDevToolbox.SystemSettings.SmtpPassword";

    /// <summary>Data Protection purpose string for off-site S3 access key id.</summary>
    public const string OffsiteAccessKeyProtectionPurpose = "ALDevToolbox.SystemSettings.OffsiteAccessKey";

    /// <summary>Data Protection purpose string for off-site S3 secret access key.</summary>
    public const string OffsiteSecretKeyProtectionPurpose = "ALDevToolbox.SystemSettings.OffsiteSecretKey";

    /// <summary>Data Protection purpose string for the deployment-wide Entra client secret.</summary>
    public const string EntraClientSecretProtectionPurpose = "ALDevToolbox.SystemSettings.EntraClientSecret";

    /// <summary>Data Protection purpose string for the GitHub App's OAuth client secret.</summary>
    public const string GitHubClientSecretProtectionPurpose = "ALDevToolbox.SystemSettings.GitHubClientSecret";

    /// <summary>Data Protection purpose string for the GitHub App's PEM private key.</summary>
    public const string GitHubPrivateKeyProtectionPurpose = "ALDevToolbox.SystemSettings.GitHubPrivateKey";

    /// <summary>
    /// Data Protection purpose string for the secret GitHub signs webhook
    /// deliveries with. Its own purpose, like every other secret on this row, so
    /// ciphertext minted for one field can never be unprotected as another.
    /// </summary>
    public const string GitHubWebhookSecretProtectionPurpose = "ALDevToolbox.SystemSettings.GitHubWebhookSecret";

    /// <summary>Discriminator for the default S3-compatible off-site backend.</summary>
    public const string S3ProviderName = "s3";

    /// <summary>Discriminator for the Azure Blob Storage off-site backend.</summary>
    public const string AzureBlobProviderName = "azure-blob";

    private readonly AppDbContext _db;
    private readonly SmtpFallbackOptions _smtpFallback;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _offsiteAccessProtector;
    private readonly IDataProtector _offsiteSecretProtector;
    private readonly IDataProtector _entraSecretProtector;
    private readonly IDataProtector _githubSecretProtector;
    private readonly IDataProtector _githubKeyProtector;
    private readonly IDataProtector _githubWebhookProtector;
    private readonly ILogger<SystemSettingsService> _logger;
    private readonly TimeProvider _clock;
    private readonly ALDevToolbox.Services.Mcp.McpAvailabilityState? _mcpAvailability;
    private readonly ALDevToolbox.Services.Tools.ToolAvailabilityState? _toolAvailability;
    private readonly IMemoryCache? _cache;
    private readonly ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue? _buildQueue;

    /// <summary>
    /// Cache key for the site banner. The banner is read on every page render
    /// by <c>MainLayout</c> / <c>AuthLayout</c>, so it must not cost a query
    /// per navigation; <see cref="SaveAsync"/> evicts the entry so a SiteAdmin
    /// edit shows up immediately rather than after the TTL.
    /// </summary>
    private const string BannerCacheKey = "system-settings:banner";

    public SystemSettingsService(
        AppDbContext db,
        IDataProtectionProvider protectionProvider,
        ILogger<SystemSettingsService> logger,
        TimeProvider clock,
        ALDevToolbox.Services.Mcp.McpAvailabilityState? mcpAvailability = null,
        ALDevToolbox.Services.Tools.ToolAvailabilityState? toolAvailability = null,
        IMemoryCache? cache = null,
        SmtpFallbackOptions? smtpFallback = null,
        ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue? buildQueue = null)
    {
        _smtpFallback = smtpFallback ?? new SmtpFallbackOptions();
        _db = db;
        _protector = protectionProvider.CreateProtector(SmtpPasswordProtectionPurpose);
        _offsiteAccessProtector = protectionProvider.CreateProtector(OffsiteAccessKeyProtectionPurpose);
        _offsiteSecretProtector = protectionProvider.CreateProtector(OffsiteSecretKeyProtectionPurpose);
        _entraSecretProtector = protectionProvider.CreateProtector(EntraClientSecretProtectionPurpose);
        _githubSecretProtector = protectionProvider.CreateProtector(GitHubClientSecretProtectionPurpose);
        _githubKeyProtector = protectionProvider.CreateProtector(GitHubPrivateKeyProtectionPurpose);
        _githubWebhookProtector = protectionProvider.CreateProtector(GitHubWebhookSecretProtectionPurpose);
        _logger = logger;
        _clock = clock;
        // Optional so existing tests that build the service by hand without
        // the toggles keep compiling. In production DI both are always set.
        _mcpAvailability = mcpAvailability;
        _toolAvailability = toolAvailability;
        _cache = cache;
        _buildQueue = buildQueue;
    }

    /// <summary>
    /// True when the deployment has more than one organisation. The settings on this
    /// row are site-wide but times are shown in each organisation's own zone, so the
    /// backup schedule page says whose zone it is showing only when there is a choice
    /// (issue #970). <c>organizations</c> is the tenant root and carries no query
    /// filter, so this count needs no fence crossing.
    /// </summary>
    public async Task<bool> HasSeveralOrganizationsAsync(CancellationToken ct = default) =>
        await _db.Organizations.AsNoTracking().Take(2).CountAsync(ct) > 1;

    /// <summary>Loads the singleton row, populating the audit-friendly view.</summary>
    public async Task<SystemSettingsView> GetViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new SystemSettingsView(
            SmtpHost: row.SmtpHost,
            SmtpPort: row.SmtpPort,
            SmtpUser: row.SmtpUser,
            HasSmtpPassword: !string.IsNullOrEmpty(row.SmtpPasswordEncrypted),
            SmtpFrom: row.SmtpFrom,
            SmtpFromName: row.SmtpFromName,
            SmtpUseStartTls: row.SmtpUseStartTls,
            BannerText: row.BannerText,
            BackupScheduleEnabled: row.BackupScheduleEnabled,
            BackupScheduleTimeUtc: row.BackupScheduleTimeUtc,
            BackupRetentionCount: row.BackupRetentionCount,
            PerTenantBackupRetentionCount: row.PerTenantBackupRetentionCount,
            DefaultStorageQuotaMb: row.DefaultStorageQuotaMb,
            IndexSizeMultiplier: row.IndexSizeMultiplier,
            McpEnabled: row.McpEnabled,
            SignupEmailDomainAllowlist: row.SignupEmailDomainAllowlist,
            ReleaseDownloadDomainAllowlist: row.ReleaseDownloadDomainAllowlist,
            DisabledTools: row.DisabledTools,
            BuildConcurrency: row.BuildConcurrency,
            UpdatedAt: row.UpdatedAt);
    }

    /// <summary>
    /// Persists changes from the SiteAdmin settings form. Validation: SMTP
    /// port (when supplied) must be 1–65535; SMTP From (when supplied) must
    /// look like an email address. Banner is unconstrained beyond a length
    /// cap. Throws <see cref="PlanValidationException"/> with field-keyed
    /// errors so the form can render them inline.
    /// </summary>
    public async Task SaveAsync(SystemSettingsInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (input.SmtpPort is int port && (port < 1 || port > 65535))
        {
            errors["SmtpPort"] = "Port must be between 1 and 65535.";
        }
        if (!string.IsNullOrWhiteSpace(input.SmtpFrom)
            && (!input.SmtpFrom.Contains('@') || input.SmtpFrom.Length > 254))
        {
            errors["SmtpFrom"] = "Enter a valid email address.";
        }
        if (input.BannerText is { Length: > 500 })
        {
            errors["BannerText"] = "Banner text must be 500 characters or fewer.";
        }
        if (input.BackupRetentionCount < 1 || input.BackupRetentionCount > 365)
        {
            errors["BackupRetentionCount"] = "Retention count must be between 1 and 365.";
        }
        if (input.PerTenantBackupRetentionCount < 1 || input.PerTenantBackupRetentionCount > 365)
        {
            errors["PerTenantBackupRetentionCount"] = "Per-tenant retention must be between 1 and 365.";
        }
        if (input.DefaultStorageQuotaMb is int quota && quota < 0)
        {
            errors["DefaultStorageQuotaMb"] = "Default quota must be 0 or greater. Leave blank for unlimited.";
        }
        if (input.IndexSizeMultiplier < 0m || input.IndexSizeMultiplier > 10m)
        {
            errors["IndexSizeMultiplier"] = "Multiplier must be between 0 and 10.";
        }
        if (input.BuildConcurrency is int builds
            && (builds < ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue.MinConcurrency
                || builds > ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue.MaxConcurrency))
        {
            errors["BuildConcurrency"] = "Enter a number from 1 to 16, or leave it empty to use the default.";
        }
        var normalisedAllowlist = NormaliseDomainAllowlist(
            input.SignupEmailDomainAllowlist, "SignupEmailDomainAllowlist", errors);
        var normalisedDownloadAllowlist = NormaliseDomainAllowlist(
            input.ReleaseDownloadDomainAllowlist, "ReleaseDownloadDomainAllowlist", errors);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);

        row.SmtpHost = NullIfBlank(input.SmtpHost);
        row.SmtpPort = input.SmtpPort;
        row.SmtpUser = NullIfBlank(input.SmtpUser);
        row.SmtpFrom = NullIfBlank(input.SmtpFrom);
        row.SmtpFromName = NullIfBlank(input.SmtpFromName);
        row.SmtpUseStartTls = input.SmtpUseStartTls;
        row.BannerText = NullIfBlank(input.BannerText);
        row.BackupScheduleEnabled = input.BackupScheduleEnabled;
        row.BackupScheduleTimeUtc = input.BackupScheduleTimeUtc;
        row.BackupRetentionCount = input.BackupRetentionCount;
        row.PerTenantBackupRetentionCount = input.PerTenantBackupRetentionCount;
        row.DefaultStorageQuotaMb = input.DefaultStorageQuotaMb;
        row.IndexSizeMultiplier = input.IndexSizeMultiplier;
        row.McpEnabled = input.McpEnabled;
        // Persist the disabled set with MCP stripped — MCP is owned by McpEnabled,
        // never the disabled_tools array. De-dup keeps the column tidy.
        row.DisabledTools = ALDevToolbox.Domain.Tools.ToolCatalog.Format(
            input.DisabledTools.Where(k => k != ALDevToolbox.Domain.Tools.ToolKey.Mcp).Distinct());
        row.SignupEmailDomainAllowlist = normalisedAllowlist;
        row.ReleaseDownloadDomainAllowlist = normalisedDownloadAllowlist;
        row.BuildConcurrency = input.BuildConcurrency;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        if (input.ClearSmtpPassword)
        {
            row.SmtpPasswordEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.SmtpPassword))
        {
            row.SmtpPasswordEncrypted = _protector.Protect(input.SmtpPassword);
        }

        await _db.SaveChangesAsync(ct);
        // The banner is read on every render from a memory cache; drop the
        // entry so this edit is visible on the next navigation.
        _cache?.Remove(BannerCacheKey);
        // Push the (possibly new) MCP toggle into the singleton so the
        // NavMenu link and the /mcp endpoint pick it up on the next render
        // without waiting for a process restart and without a per-render
        // DB hit. Synchronous, no awaiting needed.
        _mcpAvailability?.Set(row.McpEnabled);
        // Same for the per-tool site toggles — refresh the cached set so the
        // sidebar and route gate pick up the change on the next render/request.
        _toolAvailability?.Set(ALDevToolbox.Domain.Tools.ToolCatalog.ParseDisabled(row.DisabledTools));
        // And the build limit: the queue applies it at once, so a SiteAdmin resizing
        // the server does not need a restart (#1164).
        _buildQueue?.ApplySetting(row.BuildConcurrency);
        if (_buildQueue is not null)
            ALDevToolbox.Services.ObjectExplorer.Import.BuildConcurrencyAdvice.WarnIfAboveRecommendation(_buildQueue.Limit, _logger);
        _logger.LogInformation(
            "System settings updated (smtp_host={SmtpHost}, banner={HasBanner}, mcp={Mcp}, build_concurrency={BuildConcurrency}).",
            row.SmtpHost ?? "<unset>",
            !string.IsNullOrEmpty(row.BannerText),
            row.McpEnabled,
            row.BuildConcurrency?.ToString() ?? "<default>");
    }

    /// <summary>
    /// Returns the SMTP configuration the email sender should use, preferring
    /// the DB-stored override when present and falling back to env vars when
    /// the DB row is unset. Returns <see langword="null"/> when neither path
    /// yields a host + from.
    /// </summary>
    public async Task<ResolvedSmtpSettings?> ResolveSmtpAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        return TryResolveFromDb(row) ?? ResolveFromOptions();
    }

    private ResolvedSmtpSettings? TryResolveFromDb(SystemSettings? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.SmtpHost) || string.IsNullOrWhiteSpace(row.SmtpFrom))
        {
            return null;
        }

        string? plaintext = null;
        if (!string.IsNullOrEmpty(row.SmtpPasswordEncrypted))
        {
            try
            {
                plaintext = _protector.Unprotect(row.SmtpPasswordEncrypted);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Loud signal: a key-ring rotation that lost the old key would
                // otherwise silently degrade outbound mail.
                _logger.LogError(ex, "Failed to decrypt SMTP password from system_settings; falling back to env vars.");
                return null;
            }
        }

        return ResolvedSmtpSettings.TryFrom(
            host: row.SmtpHost,
            port: row.SmtpPort,
            user: row.SmtpUser,
            password: plaintext,
            from: row.SmtpFrom,
            fromName: row.SmtpFromName,
            useStartTls: row.SmtpUseStartTls);
    }

    /// <summary>
    /// The deployment's SMTP settings, used until an admin fills in the
    /// SiteAdmin form. The password is read from the file the options name, not
    /// carried in the options themselves.
    /// </summary>
    private ResolvedSmtpSettings? ResolveFromOptions() =>
        ResolvedSmtpSettings.TryFrom(
            host: _smtpFallback.Host,
            port: _smtpFallback.Port,
            user: _smtpFallback.User,
            password: ReadSecret(_smtpFallback.PasswordFile),
            from: _smtpFallback.From,
            fromName: _smtpFallback.FromName,
            useStartTls: _smtpFallback.UseStartTls);

    /// <summary>Loads the off-site backup settings for the SiteAdmin form (no plaintext keys).</summary>
    public async Task<OffsiteSettingsView> GetOffsiteViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new OffsiteSettingsView(
            Enabled: row.OffsiteBackupEnabled,
            Provider: NormaliseProvider(row.OffsiteProvider),
            Endpoint: row.OffsiteEndpoint,
            Region: row.OffsiteRegion,
            Bucket: row.OffsiteBucket,
            Prefix: row.OffsitePrefix,
            HasAccessKey: !string.IsNullOrEmpty(row.OffsiteAccessKeyEncrypted),
            HasSecretKey: !string.IsNullOrEmpty(row.OffsiteSecretKeyEncrypted),
            ForcePathStyle: row.OffsiteForcePathStyle,
            RetentionDays: row.OffsiteRetentionDays);
    }

    /// <summary>
    /// Persists off-site backup settings. When keys are supplied they're
    /// encrypted via the Data Protection ring; empty keys leave the stored
    /// value untouched. Throws <see cref="PlanValidationException"/> with
    /// field-keyed errors so the form can render them inline.
    /// </summary>
    public async Task SaveOffsiteAsync(OffsiteSettingsInput input, CancellationToken ct = default)
    {
        var provider = NormaliseProvider(input.Provider);
        var errors = new Dictionary<string, string>();
        if (input.Enabled)
        {
            if (string.IsNullOrWhiteSpace(input.Bucket))
                errors["OffsiteBucket"] = provider == AzureBlobProviderName
                    ? "Container is required when off-site backup is enabled."
                    : "Bucket is required when off-site backup is enabled.";
        }
        if (input.RetentionDays < 1 || input.RetentionDays > 3650)
        {
            errors["OffsiteRetentionDays"] = "Off-site retention must be between 1 and 3650 days.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);
        row.OffsiteBackupEnabled = input.Enabled;
        row.OffsiteProvider = provider;
        row.OffsiteEndpoint = NullIfBlank(input.Endpoint);
        row.OffsiteRegion = NullIfBlank(input.Region);
        row.OffsiteBucket = NullIfBlank(input.Bucket);
        row.OffsitePrefix = NullIfBlank(input.Prefix);
        row.OffsiteForcePathStyle = input.ForcePathStyle;
        row.OffsiteRetentionDays = input.RetentionDays;

        if (input.ClearAccessKey) row.OffsiteAccessKeyEncrypted = null;
        else if (!string.IsNullOrEmpty(input.AccessKey))
            row.OffsiteAccessKeyEncrypted = _offsiteAccessProtector.Protect(input.AccessKey);

        if (input.ClearSecretKey) row.OffsiteSecretKeyEncrypted = null;
        else if (!string.IsNullOrEmpty(input.SecretKey))
            row.OffsiteSecretKeyEncrypted = _offsiteSecretProtector.Protect(input.SecretKey);

        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Off-site backup settings updated (enabled={Enabled}, bucket={Bucket}).",
            row.OffsiteBackupEnabled, row.OffsiteBucket ?? "<unset>");
    }

    /// <summary>Loads the deployment-wide Entra app registration for the SiteAdmin form (no plaintext secret).</summary>
    public async Task<EntraAppView> GetEntraAppViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new EntraAppView(
            ClientId: row.EntraClientId,
            HasClientSecret: !string.IsNullOrEmpty(row.EntraClientSecretEncrypted));
    }

    /// <summary>
    /// Persists the deployment-wide Entra app registration. The client id
    /// must be the registration's application (client) id — a GUID. Clearing
    /// the client id also clears the stored secret, since a secret is
    /// meaningless without the registration it belongs to.
    /// </summary>
    public async Task SaveEntraAppAsync(EntraAppInput input, CancellationToken ct = default)
    {
        var clientId = NullIfBlank(input.ClientId)?.Trim();
        var errors = new Dictionary<string, string>();
        if (clientId is not null && !Guid.TryParse(clientId, out _))
        {
            errors["EntraClientId"] = "Enter the app registration's Application (client) ID - a GUID like 00000000-0000-0000-0000-000000000000.";
        }
        if (clientId is null && !string.IsNullOrEmpty(input.ClientSecret))
        {
            errors["EntraClientSecret"] = "Enter the Application (client) ID before saving a client secret.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);
        row.EntraClientId = clientId?.ToLowerInvariant();
        if (clientId is null || input.ClearClientSecret)
        {
            row.EntraClientSecretEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.ClientSecret))
        {
            row.EntraClientSecretEncrypted = _entraSecretProtector.Protect(input.ClientSecret);
        }
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Deployment-wide Entra app registration updated (client_id={ClientId}, has_secret={HasSecret}).",
            row.EntraClientId ?? "<unset>", !string.IsNullOrEmpty(row.EntraClientSecretEncrypted));
    }

    // ── Deployment-wide GitHub App (see .design/github-integration.md) ──────

    /// <summary>Loads the GitHub App registration for the SiteAdmin form (no plaintext secrets).</summary>
    public async Task<GitHubAppView> GetGitHubAppViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new GitHubAppView(
            AppId: row.GitHubAppId,
            AppSlug: row.GitHubAppSlug,
            ClientId: row.GitHubClientId,
            HasClientSecret: !string.IsNullOrEmpty(row.GitHubClientSecretEncrypted),
            HasPrivateKey: !string.IsNullOrEmpty(row.GitHubPrivateKeyEncrypted),
            HasWebhookSecret: !string.IsNullOrEmpty(row.GitHubWebhookSecretEncrypted));
    }

    /// <summary>
    /// HTML <c>pattern</c> for the GitHub App id: a positive whole number.
    /// Mirrored onto the field on <c>/site-admin/settings/github</c> so the
    /// browser catches the obvious cases before the post, exactly as CLAUDE.md
    /// asks — keep the two in step.
    /// </summary>
    public const string GitHubAppIdPattern = "[1-9][0-9]{0,18}";

    /// <summary>
    /// HTML <c>pattern</c> for the GitHub App slug: letters, digits and inner
    /// hyphens — the same shape GitHub puts in <c>github.com/apps/{slug}</c>.
    /// Validated so a pasted full URL is rejected here rather than producing a
    /// 404 install link the admin has to debug. <see cref="GitHubAppSlugRegex"/>
    /// is built from this constant, so the browser rule and the server rule are
    /// one string rather than two that can drift.
    ///
    /// <para>The hyphen is escaped for the browser's sake: <c>pattern</c> is
    /// compiled with the RegExp <c>v</c> flag, under which a bare <c>-</c>
    /// inside a character class is a syntax error - and a pattern that does not
    /// compile is dropped silently rather than reported.</para>
    /// </summary>
    public const string GitHubAppSlugPattern = @"[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?";

    private static readonly System.Text.RegularExpressions.Regex GitHubAppSlugRegex = new(
        $"^{GitHubAppSlugPattern}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Persists the deployment-wide GitHub App registration. Validation: the
    /// app id must be a positive whole number, the slug must look like a slug,
    /// and the private key must be a PEM the runtime can actually import — a
    /// key that fails here would otherwise fail on the first install, far from
    /// the form that accepted it. Clearing the app id clears the slug, client
    /// id and both secrets with it, since none of them mean anything without
    /// the App. Throws <see cref="PlanValidationException"/> with field-keyed
    /// errors so the form renders them inline.
    /// </summary>
    public async Task SaveGitHubAppAsync(GitHubAppInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();

        long? appId = null;
        var rawAppId = NullIfBlank(input.AppId);
        if (rawAppId is not null)
        {
            if (!long.TryParse(rawAppId, out var parsed) || parsed <= 0)
            {
                errors["GitHubAppId"] = "Enter the App ID from the app's settings page on GitHub - a whole number like 123456.";
            }
            else
            {
                appId = parsed;
            }
        }

        var slug = NullIfBlank(input.AppSlug)?.ToLowerInvariant();
        if (slug is not null && (slug.Length > 120 || !GitHubAppSlugRegex.IsMatch(slug)))
        {
            errors["GitHubAppSlug"] = "Enter just the app's name as it appears at the end of its GitHub URL, like al-workbench.";
        }
        else if (slug is null && rawAppId is not null)
        {
            // Without the slug there is no install URL, so an app id on its own
            // leaves every organisation with a Connect button that goes nowhere.
            errors["GitHubAppSlug"] = "Enter the app's name as it appears at the end of its GitHub URL - without it, nobody can install the app.";
        }

        var clientId = NullIfBlank(input.ClientId);
        var privateKey = NullIfBlank(input.PrivateKeyPem);
        if (privateKey is not null && !TryImportPrivateKey(privateKey))
        {
            errors["GitHubPrivateKey"] = "That does not look like the private key file GitHub gave you. Paste the whole file, including the BEGIN and END lines.";
        }

        if (rawAppId is null && (privateKey is not null || !string.IsNullOrEmpty(input.ClientSecret)
            || !string.IsNullOrEmpty(input.WebhookSecret)))
        {
            errors["GitHubAppId"] = "Enter the App ID before saving the private key, the client secret or the webhook secret.";
        }
        if (clientId is null && !string.IsNullOrEmpty(input.ClientSecret))
        {
            errors["GitHubClientSecret"] = "Enter the Client ID before saving a client secret.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);
        row.GitHubAppId = appId;
        row.GitHubAppSlug = appId is null ? null : slug;
        row.GitHubClientId = appId is null ? null : clientId;

        if (appId is null || input.ClearClientSecret)
        {
            row.GitHubClientSecretEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.ClientSecret))
        {
            row.GitHubClientSecretEncrypted = _githubSecretProtector.Protect(input.ClientSecret);
        }

        if (appId is null || input.ClearPrivateKey)
        {
            row.GitHubPrivateKeyEncrypted = null;
        }
        else if (privateKey is not null)
        {
            row.GitHubPrivateKeyEncrypted = _githubKeyProtector.Protect(privateKey);
        }

        // Same three branches as the two secrets above: clearing the App (or the
        // explicit Forget tick) wipes it, a value replaces it, and a blank field
        // leaves the stored one alone so a save that does not retype it keeps
        // deliveries verifiable.
        if (appId is null || input.ClearWebhookSecret)
        {
            row.GitHubWebhookSecretEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.WebhookSecret))
        {
            row.GitHubWebhookSecretEncrypted = _githubWebhookProtector.Protect(input.WebhookSecret);
        }

        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Deployment-wide GitHub App registration updated (app_id={AppId}, slug={Slug}, has_key={HasKey}, has_secret={HasSecret}, has_webhook_secret={HasWebhookSecret}).",
            row.GitHubAppId?.ToString() ?? "<unset>",
            row.GitHubAppSlug ?? "<unset>",
            !string.IsNullOrEmpty(row.GitHubPrivateKeyEncrypted),
            !string.IsNullOrEmpty(row.GitHubClientSecretEncrypted),
            !string.IsNullOrEmpty(row.GitHubWebhookSecretEncrypted));
    }

    /// <summary>
    /// The plaintext secret GitHub signs webhook deliveries with, or
    /// <see langword="null"/> when none is stored or the key ring can no longer
    /// read it.
    ///
    /// <para>Deliberately narrower than <see cref="ResolveGitHubAppAsync"/>: the
    /// signature check is the very first thing an anonymous inbound request meets,
    /// and it has nothing to do with whether the App id and private key happen to
    /// be filled in. Null here means every delivery is refused, which is the safe
    /// direction. See <c>.design/github-integration-phase2.md</c> (#627).</para>
    /// </summary>
    public async Task<string?> ResolveGitHubWebhookSecretAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (string.IsNullOrEmpty(row?.GitHubWebhookSecretEncrypted)) return null;
        try
        {
            return _githubWebhookProtector.Unprotect(row.GitHubWebhookSecretEncrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt the GitHub webhook secret; deliveries will be refused until it is re-entered.");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="pem"/> is a PEM the runtime can load as an RSA
    /// private key. GitHub hands out PKCS#1 (<c>BEGIN RSA PRIVATE KEY</c>);
    /// <see cref="System.Security.Cryptography.RSA.ImportFromPem"/> also accepts
    /// PKCS#8, so both paste cleanly.
    /// </summary>
    private static bool TryImportPrivateKey(string pem)
    {
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>
    /// Decrypts the stored GitHub App credentials. Returns <see langword="null"/>
    /// when the deployment has no app id or no private key, or when the key can't
    /// be decrypted (a lost key ring) — in every case the honest answer is "GitHub
    /// isn't available here", and the caller renders that rather than a stack trace.
    /// </summary>
    public async Task<ResolvedGitHubApp?> ResolveGitHubAppAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row?.GitHubAppId is not long appId) return null;
        if (string.IsNullOrEmpty(row.GitHubPrivateKeyEncrypted)) return null;

        string privateKey;
        string? clientSecret = null;
        try
        {
            privateKey = _githubKeyProtector.Unprotect(row.GitHubPrivateKeyEncrypted);
            if (!string.IsNullOrEmpty(row.GitHubClientSecretEncrypted))
            {
                clientSecret = _githubSecretProtector.Unprotect(row.GitHubClientSecretEncrypted);
            }
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt GitHub App credentials; GitHub features disabled until re-entered.");
            return null;
        }

        return new ResolvedGitHubApp(
            AppId: appId,
            AppSlug: NullIfBlank(row.GitHubAppSlug),
            ClientId: NullIfBlank(row.GitHubClientId),
            ClientSecret: clientSecret,
            PrivateKeyPem: privateKey);
    }

    /// <summary>
    /// Decrypts the stored credentials and returns a fully resolved
    /// configuration ready for the S3 SDK. Returns <see langword="null"/>
    /// when off-site is disabled, the bucket isn't set, or either key is
    /// missing / undecryptable.
    /// </summary>
    public async Task<ResolvedOffsiteSettings?> ResolveOffsiteAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null || !row.OffsiteBackupEnabled) return null;
        if (string.IsNullOrWhiteSpace(row.OffsiteBucket)) return null;
        if (string.IsNullOrEmpty(row.OffsiteAccessKeyEncrypted) || string.IsNullOrEmpty(row.OffsiteSecretKeyEncrypted)) return null;
        string accessKey, secretKey;
        try
        {
            accessKey = _offsiteAccessProtector.Unprotect(row.OffsiteAccessKeyEncrypted);
            secretKey = _offsiteSecretProtector.Unprotect(row.OffsiteSecretKeyEncrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt off-site credentials; off-site backup disabled until re-entered.");
            return null;
        }
        return new ResolvedOffsiteSettings(
            Provider: NormaliseProvider(row.OffsiteProvider),
            Endpoint: NullIfBlank(row.OffsiteEndpoint),
            Region: NullIfBlank(row.OffsiteRegion),
            Bucket: row.OffsiteBucket!,
            Prefix: NullIfBlank(row.OffsitePrefix),
            AccessKey: accessKey,
            SecretKey: secretKey,
            ForcePathStyle: row.OffsiteForcePathStyle,
            RetentionDays: row.OffsiteRetentionDays);
    }

    /// <summary>
    /// Returns the system banner text, or <see langword="null"/> when none is
    /// set. Memory-cached for a minute because every page render asks for it;
    /// <see cref="SaveAsync"/> evicts the entry, so a SiteAdmin edit is live on
    /// the next navigation rather than a minute later.
    /// </summary>
    public async Task<string?> GetBannerAsync(CancellationToken ct = default)
    {
        if (_cache is not null && _cache.TryGetValue(BannerCacheKey, out string? cached))
        {
            return cached;
        }

        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        var banner = string.IsNullOrWhiteSpace(row?.BannerText) ? null : row!.BannerText;
        _cache?.Set(BannerCacheKey, banner, TimeSpan.FromMinutes(1));
        return banner;
    }

    /// <summary>
    /// Returns the configured site-wide email-domain allow-list, or
    /// <see langword="null"/> when the SiteAdmin hasn't set one (feature off
    /// — any email domain may sign up). Domains are returned lowercased and
    /// trimmed; callers compare with ordinal equality.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetSignupAllowedDomainsAsync(CancellationToken ct = default)
    {
        var raw = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.SignupEmailDomainAllowlist)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var list = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 ? null : list;
    }

    /// <summary>
    /// True when the SiteAdmin has enabled the MCP server on this
    /// deployment. The MCP endpoint and the Tools menu's "MCP" link both
    /// hide themselves when this returns <c>false</c>, regardless of the
    /// deployment-level <c>Mcp:Enabled</c> in appsettings.
    /// </summary>
    public async Task<bool> IsMcpEnabledAsync(CancellationToken ct = default)
    {
        return await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.McpEnabled)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<SystemSettings> LoadAsync(CancellationToken ct)
    {
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            // Defensive: the migration inserts the singleton row, but tests
            // that bypass migrations (or future databases that don't run the
            // seed Sql) shouldn't NRE. Insert-once-on-demand keeps GetViewAsync
            // and SaveAsync robust without changing the deployment story.
            row = new SystemSettings
            {
                Id = 1,
                UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.SystemSettings.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        return row;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Coerces an off-site provider discriminator to a known value, defaulting
    /// to <see cref="S3ProviderName"/>. Keeps a blank column (pre-migration
    /// rows / hand-built test settings) and any unrecognised value safely on
    /// the S3 path rather than failing the resolve.
    /// </summary>
    private static string NormaliseProvider(string? value) =>
        string.Equals(value, AzureBlobProviderName, StringComparison.OrdinalIgnoreCase)
            ? AzureBlobProviderName
            : S3ProviderName;

    private static readonly System.Text.RegularExpressions.Regex AllowlistDomainRegex = new(
        "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses the raw textarea contents into a canonical newline-joined
    /// list of lowercased domains. Splits on newlines / commas / whitespace,
    /// trims, drops blanks, and rejects entries that don't look like a bare
    /// domain. Errors are keyed on <paramref name="fieldKey"/> so the form can
    /// render them inline. Returns <see langword="null"/> for blank input
    /// (feature off).
    /// </summary>
    private static string? NormaliseDomainAllowlist(string? raw, string fieldKey, Dictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = raw.Split(new[] { '\n', '\r', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var lowered = token.ToLowerInvariant();
            if (lowered.StartsWith('@')) lowered = lowered[1..];
            if (!AllowlistDomainRegex.IsMatch(lowered) || lowered.Length > 253)
            {
                errors[fieldKey] = $"'{token}' isn't a valid bare domain. Use entries like 'cronus.com', one per line.";
                return null;
            }
            if (seen.Add(lowered))
            {
                keep.Add(lowered);
            }
        }
        return keep.Count == 0 ? null : string.Join('\n', keep);
    }

    /// <summary>
    /// Returns the configured host allow-list for the Object Explorer release
    /// "import from URL" flow, or <see langword="null"/> when the SiteAdmin
    /// hasn't set one (feature off — no URL download is permitted). Hosts are
    /// returned lowercased and trimmed.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetReleaseDownloadAllowedHostsAsync(CancellationToken ct = default)
    {
        var raw = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ReleaseDownloadDomainAllowlist)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var list = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 ? null : list;
    }

    /// <summary>
    /// Suffix-matches <paramref name="host"/> against an allow-list entry: the
    /// host is permitted when it equals an entry or is a subdomain of one
    /// (so <c>microsoft.com</c> covers <c>download.microsoft.com</c>).
    /// Comparison is case-insensitive; a null/empty list permits nothing.
    /// </summary>
    public static bool IsHostAllowed(string? host, IReadOnlyList<string>? allowlist)
    {
        if (string.IsNullOrWhiteSpace(host) || allowlist is null || allowlist.Count == 0) return false;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var entry in allowlist)
        {
            var e = entry.Trim().TrimEnd('.').ToLowerInvariant();
            if (e.Length == 0) continue;
            if (h == e || h.EndsWith("." + e, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool? ParseBool(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSecret(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return File.ReadAllText(path).Trim();
    }
}
