namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Singleton row (id pinned to <c>1</c>) holding cross-organisation
/// configuration: SMTP override, system banner, and the default signup
/// approval policy. The SMTP password is stored as Data-Protection
/// ciphertext; the audit interceptor redacts the column to a fixed
/// sentinel rather than capturing ciphertext history.
/// </summary>
public class SystemSettings
{
    /// <summary>Pinned to <c>1</c>. The migration inserts the row; nothing else creates one.</summary>
    public int Id { get; set; }

    /// <summary>SMTP host override. Empty/null means "fall back to <c>SMTP_HOST</c> env var".</summary>
    public string? SmtpHost { get; set; }

    public int? SmtpPort { get; set; }

    public string? SmtpUser { get; set; }

    /// <summary>
    /// Data-Protection-encrypted SMTP password. Decryption is contained in
    /// <see cref="Services.SystemSettingsService"/>; plaintext never
    /// leaves that service boundary.
    /// </summary>
    public string? SmtpPasswordEncrypted { get; set; }

    public string? SmtpFrom { get; set; }

    /// <summary>
    /// Optional display name paired with <see cref="SmtpFrom"/> on the
    /// message envelope (e.g. <c>"AL Workbench" &lt;noreply@example.com&gt;</c>).
    /// Empty/null sends with the bare address as both name and address.
    /// </summary>
    public string? SmtpFromName { get; set; }

    public bool? SmtpUseStartTls { get; set; }

    /// <summary>Free-text banner displayed at the top of every page when set.</summary>
    public string? BannerText { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>BackupScheduler</c> takes a daily
    /// backup at <see cref="BackupScheduleTimeUtc"/>. Operators can pause
    /// the schedule without losing the time-of-day setting.
    /// </summary>
    public bool BackupScheduleEnabled { get; set; } = true;

    /// <summary>
    /// UTC time-of-day for the daily backup. Stored as
    /// <see cref="TimeOnly"/> so the column type is <c>time</c>, which
    /// drops the timezone-aware drift that plagued the original
    /// timestamp-with-time-zone shape during prototyping.
    /// </summary>
    public TimeOnly BackupScheduleTimeUtc { get; set; } = new(2, 0);

    /// <summary>
    /// Number of unpinned backups retained on disk. Older unpinned files
    /// are pruned after each successful backup. Pinned backups are
    /// exempt and never counted toward this cap.
    /// </summary>
    public int BackupRetentionCount { get; set; } = 14;

    /// <summary>
    /// Number of per-tenant snapshots retained on disk per organisation.
    /// Independent of <see cref="BackupRetentionCount"/> so SiteAdmins can
    /// keep a longer "restore to yesterday" tail (e.g. 30 days) without
    /// holding 30 full pg_dumps. Pinned snapshots are exempt.
    /// </summary>
    public int PerTenantBackupRetentionCount { get; set; } = 30;

    /// <summary>
    /// Default storage quota (in megabytes) applied to organisations that
    /// have no per-org override. Null means unlimited. The
    /// <c>StorageQuotaGuard</c> hard-blocks tenant writes once the
    /// organisation's billable usage reaches the effective quota.
    /// </summary>
    public int? DefaultStorageQuotaMb { get; set; }

    /// <summary>
    /// Weight applied to per-org index/metadata bytes when computing the
    /// billable size for quota checks: <c>billable = logical + multiplier *
    /// index</c>. Lets operators charge primarily for logical data while
    /// still soft-accounting for index/metadata overhead. Default 0.5.
    /// </summary>
    public decimal IndexSizeMultiplier { get; set; } = 0.5m;

    /// <summary>
    /// When <see langword="true"/>, the scheduler uploads every successful
    /// scheduled full pg_dump to the configured S3-compatible bucket.
    /// Failed uploads log and continue; the local file is the source of
    /// truth for restorability.
    /// </summary>
    public bool OffsiteBackupEnabled { get; set; }

    /// <summary>S3 endpoint URL. Null when using AWS's default endpoint for the region.</summary>
    public string? OffsiteEndpoint { get; set; }

    public string? OffsiteRegion { get; set; }

    public string? OffsiteBucket { get; set; }

    /// <summary>Object key prefix inside the bucket; empty/null means "root of the bucket".</summary>
    public string? OffsitePrefix { get; set; }

    /// <summary>
    /// Data-Protection-encrypted S3 access key id. Decryption happens
    /// inside <see cref="Services.OffsiteBackupService"/>; plaintext
    /// never leaves that service boundary.
    /// </summary>
    public string? OffsiteAccessKeyEncrypted { get; set; }

    /// <summary>Data-Protection-encrypted S3 secret access key.</summary>
    public string? OffsiteSecretKeyEncrypted { get; set; }

    /// <summary>
    /// Set to <see langword="true"/> for MinIO and other S3-compatible
    /// servers that don't support virtual-hosted–style addressing.
    /// </summary>
    public bool OffsiteForcePathStyle { get; set; }

    /// <summary>Objects older than this many days are pruned from the bucket. Default 90.</summary>
    public int OffsiteRetentionDays { get; set; } = 90;

    /// <summary>
    /// Off-site storage backend: <c>"s3"</c> (default, S3-compatible — AWS,
    /// MinIO, R2, B2) or <c>"azure-blob"</c> (Azure Blob Storage). Selects
    /// which <see cref="Services.Offsite.IOffsiteStorageProvider"/> the
    /// <see cref="Services.OffsiteBackupService"/> drives. For Azure the
    /// access-key column holds the storage account name and the secret-key
    /// column holds the account key; <see cref="OffsiteBucket"/> is the
    /// container name. Region and <see cref="OffsiteForcePathStyle"/> are
    /// S3-only and ignored for Azure.
    /// </summary>
    public string OffsiteProvider { get; set; } = "s3";

    /// <summary>
    /// SiteAdmin runtime toggle for the MCP server. The deployment-level
    /// <c>Mcp:Enabled</c> setting in appsettings still controls whether
    /// the route is mapped at startup; this flag lets SiteAdmins flip
    /// MCP off without redeploying when an incident makes it useful.
    /// Defaults to <see langword="false"/> — a fresh install opts in
    /// explicitly via <c>/site-admin/settings</c>.
    /// </summary>
    public bool McpEnabled { get; set; }

    /// <summary>
    /// Tools switched off site-wide, stored as <see cref="Domain.Tools.ToolKey"/>
    /// names (e.g. <c>"Projects"</c>). Empty by default — every tool is on until
    /// a SiteAdmin turns one off on <c>/site-admin/settings/tools</c>, and a
    /// site-disabled tool can't be re-enabled per-org. MCP isn't listed here; it
    /// keeps its own <see cref="McpEnabled"/> flag.
    /// </summary>
    public List<string> DisabledTools { get; set; } = new();

    /// <summary>
    /// Newline-delimited list of bare email domains permitted to sign up
    /// (e.g. <c>"cronus.com\nexample.dk"</c>). <see langword="null"/> or empty
    /// means "feature off, any email domain is allowed" — the SiteAdmin
    /// opts in by filling the form. Exact-match only; subdomains must be
    /// listed explicitly.
    /// </summary>
    public string? SignupEmailDomainAllowlist { get; set; }

    /// <summary>
    /// Newline-delimited list of bare hosts that the Object Explorer release
    /// "import from URL" flow may download from (e.g.
    /// <c>"download.microsoft.com"</c>). <see langword="null"/> or empty means
    /// "no host allowed" — URL import stays disabled until a SiteAdmin opts in.
    /// Matched by suffix: an entry <c>microsoft.com</c> also permits
    /// <c>download.microsoft.com</c>. Deliberately looser than
    /// <see cref="SignupEmailDomainAllowlist"/>'s exact match.
    /// </summary>
    public string? ReleaseDownloadDomainAllowlist { get; set; }

    /// <summary>
    /// Client id of the deployment-wide Entra app registration used for
    /// Microsoft sign-in by every org that hasn't supplied its own (see
    /// <see cref="OrganizationSettings.EntraClientId"/>). Null until a
    /// SiteAdmin fills the form; Microsoft sign-in stays unavailable
    /// deployment-wide without it.
    /// </summary>
    public string? EntraClientId { get; set; }

    /// <summary>
    /// Data-Protection-encrypted client secret for the deployment-wide app
    /// registration. Decryption is contained in
    /// <see cref="Services.SystemSettingsService"/>; the audit interceptor
    /// redacts the column.
    /// </summary>
    public string? EntraClientSecretEncrypted { get; set; }

    /// <summary>
    /// Numeric id of the GitHub App registered for this deployment. Null until
    /// a SiteAdmin fills the form; no organisation can connect a GitHub
    /// organisation without it. Signed into the App JWT as <c>iss</c>.
    /// See <c>.design/github-integration.md</c>.
    /// </summary>
    public long? GitHubAppId { get; set; }

    /// <summary>
    /// The GitHub App's URL slug, used to build the install link
    /// (<c>https://github.com/apps/{slug}/installations/new</c>). Stored
    /// separately from the id because GitHub exposes only the slug in that URL.
    /// </summary>
    public string? GitHubAppSlug { get; set; }

    /// <summary>
    /// OAuth client id of the same GitHub App, used by the per-user
    /// account-link handshake (issue #621).
    /// </summary>
    public string? GitHubClientId { get; set; }

    /// <summary>
    /// Data-Protection-encrypted OAuth client secret paired with
    /// <see cref="GitHubClientId"/>. Decryption is contained in
    /// <see cref="Services.SystemSettingsService"/>; the audit interceptor
    /// redacts the column.
    /// </summary>
    public string? GitHubClientSecretEncrypted { get; set; }

    /// <summary>
    /// Data-Protection-encrypted PEM private key of the GitHub App. Used only
    /// to sign the short-lived App JWT that mints installation tokens; the
    /// audit interceptor redacts the column, and losing the key ring means
    /// re-entering the key.
    /// </summary>
    public string? GitHubPrivateKeyEncrypted { get; set; }

    /// <summary>
    /// Data-Protection-encrypted secret GitHub signs webhook deliveries with
    /// (<c>X-Hub-Signature-256</c>). Deployment-wide, like the rest of the App
    /// registration, because one App has one webhook. Null means the workbench
    /// accepts no deliveries at all - an unverifiable delivery is refused, never
    /// trusted. The audit interceptor redacts the column. See
    /// <c>.design/github-integration-phase2.md</c> (#627).
    /// </summary>
    public string? GitHubWebhookSecretEncrypted { get; set; }

    /// <summary>
    /// How many project and pull request builds run at once, set by a SiteAdmin on
    /// <c>/site-admin/settings/builds</c> (1 to 16). Null means "use the default":
    /// <c>OE_BUILD_CONCURRENCY</c>, else 2. Applied to the running build queue on
    /// save, without a restart (#1164).
    /// </summary>
    public int? BuildConcurrency { get; set; }

    public DateTime UpdatedAt { get; set; }
}
