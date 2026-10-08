using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Builds a <see cref="SystemSettingsInput"/> by overlaying the fields
/// for one settings section onto the values already in the database.
/// The split lets each settings sub-page POST only its own fields while
/// still calling <see cref="SystemSettingsService.SaveAsync"/>, which
/// does the single-row write and validation.
/// </summary>
internal static class SettingsInputBuilder
{
    public static SystemSettingsInput WithSmtp(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        SmtpHost = form["SmtpHost"].ToString(),
        SmtpPort = int.TryParse(form["SmtpPort"], out var port) ? port : null,
        SmtpUser = form["SmtpUser"].ToString(),
        SmtpPassword = form["SmtpPassword"].ToString(),
        ClearSmtpPassword = IsChecked(form, "ClearSmtpPassword"),
        SmtpFrom = form["SmtpFrom"].ToString(),
        SmtpFromName = form["SmtpFromName"].ToString(),
        // The SMTP form always posts this (the switch carries a paired hidden
        // false), so "absent" no longer has to mean "leave it alone" -- Base()
        // already carries the stored value across for the tabs that don't post
        // it. Before the pair existed, an unticked box simply vanished from the
        // form, saved null, and ResolvedSmtpSettings read null as `?? true`:
        // turning STARTTLS off in the UI silently left it on.
        SmtpUseStartTls = form.ContainsKey("SmtpUseStartTls") ? IsChecked(form, "SmtpUseStartTls") : null,
    };

    /// <summary>
    /// The backups tab. The time of day is typed in the organisation's display zone
    /// (issue #970) and stored as UTC. The page posts the offset it showed the time
    /// with, so an unchanged value converts back with exactly that offset even if the
    /// clocks changed between rendering and saving; <paramref name="displayOffset"/>
    /// (the zone's offset now) is the fallback when that field is missing or odd.
    /// </summary>
    public static SystemSettingsInput WithBackups(SystemSettingsView current, IFormCollection form, TimeSpan displayOffset) => Base(current) with
    {
        BackupScheduleEnabled = IsChecked(form, "BackupScheduleEnabled"),
        BackupScheduleTimeUtc = TimeOnly.TryParse(form["BackupScheduleTime"], System.Globalization.CultureInfo.InvariantCulture, out var bst)
            ? DisplayTimeZone.TimeOfDayToUtc(bst, PostedOffset(form) ?? displayOffset)
            : current.BackupScheduleTimeUtc,
        BackupRetentionCount = int.TryParse(form["BackupRetentionCount"], out var brc)
            ? brc
            : current.BackupRetentionCount,
        PerTenantBackupRetentionCount = int.TryParse(form["PerTenantBackupRetentionCount"], out var ptrc)
            ? ptrc
            : current.PerTenantBackupRetentionCount,
    };

    public static SystemSettingsInput WithQuotas(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        DefaultStorageQuotaMb = string.IsNullOrWhiteSpace(form["DefaultStorageQuotaMb"])
            ? null
            : int.TryParse(form["DefaultStorageQuotaMb"], out var dsq) ? dsq : null,
        IndexSizeMultiplier = decimal.TryParse(
            form["IndexSizeMultiplier"],
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var ism)
                ? ism
                : current.IndexSizeMultiplier,
    };

    public static SystemSettingsInput WithGeneral(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        BannerText = form["BannerText"].ToString(),
        SignupEmailDomainAllowlist = form["SignupEmailDomainAllowlist"].ToString(),
        ReleaseDownloadDomainAllowlist = form["ReleaseDownloadDomainAllowlist"].ToString(),
    };

    /// <summary>
    /// Overlays the Tools tab. Each tool has a checkbox <c>tool_&lt;Key&gt;</c> that's
    /// checked when the tool is <em>on</em>; an unchecked (so unposted) box means
    /// the tool is disabled. MCP is part of the same grid but maps to
    /// <see cref="SystemSettingsInput.McpEnabled"/>, not the disabled set.
    /// </summary>
    public static SystemSettingsInput WithTools(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        McpEnabled = IsChecked(form, $"tool_{ToolKey.Mcp}"),
        DisabledTools = ToolCatalog.All
            .Where(t => t.Key != ToolKey.Mcp && !IsChecked(form, $"tool_{t.Key}"))
            .Select(t => t.Key)
            .ToList(),
    };

    /// <summary>
    /// The Builds tab. An empty box clears the value, so the default applies again;
    /// a value that is not a whole number is passed on as 0, which the service
    /// refuses with the field's own message rather than silently clearing it.
    /// </summary>
    public static SystemSettingsInput WithBuilds(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        BuildConcurrency = string.IsNullOrWhiteSpace(form["BuildConcurrency"])
            ? null
            : int.TryParse(form["BuildConcurrency"], out var bc) ? bc : 0,
    };

    /// <summary>
    /// Carries every field from the current view across into an Input —
    /// the per-section overlays then use <c>with</c> to mutate just the
    /// fields they own. SMTP password is intentionally left empty +
    /// ClearSmtpPassword=false so a non-SMTP save never disturbs the
    /// encrypted password column.
    /// </summary>
    private static SystemSettingsInput Base(SystemSettingsView current) => new(
        SmtpHost: current.SmtpHost,
        SmtpPort: current.SmtpPort,
        SmtpUser: current.SmtpUser,
        SmtpPassword: null,
        ClearSmtpPassword: false,
        SmtpFrom: current.SmtpFrom,
        SmtpFromName: current.SmtpFromName,
        SmtpUseStartTls: current.SmtpUseStartTls,
        BannerText: current.BannerText,
        BackupScheduleEnabled: current.BackupScheduleEnabled,
        BackupScheduleTimeUtc: current.BackupScheduleTimeUtc,
        BackupRetentionCount: current.BackupRetentionCount,
        PerTenantBackupRetentionCount: current.PerTenantBackupRetentionCount,
        DefaultStorageQuotaMb: current.DefaultStorageQuotaMb,
        IndexSizeMultiplier: current.IndexSizeMultiplier,
        McpEnabled: current.McpEnabled,
        SignupEmailDomainAllowlist: current.SignupEmailDomainAllowlist,
        ReleaseDownloadDomainAllowlist: current.ReleaseDownloadDomainAllowlist,
        DisabledTools: ToolCatalog.ParseDisabled(current.DisabledTools).ToList(),
        BuildConcurrency: current.BuildConcurrency);

    /// <summary>
    /// The offset the backups page showed its time with, in minutes; null when
    /// absent or outside the range any real zone uses (UTC-14 to UTC+14).
    /// </summary>
    private static TimeSpan? PostedOffset(IFormCollection form) =>
        int.TryParse(form["BackupScheduleOffsetMinutes"], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            && Math.Abs(minutes) <= 14 * 60
            ? TimeSpan.FromMinutes(minutes)
            : null;

    private static bool IsChecked(IFormCollection form, string name) =>
        EndpointHelpers.IsChecked(form, name);
}
