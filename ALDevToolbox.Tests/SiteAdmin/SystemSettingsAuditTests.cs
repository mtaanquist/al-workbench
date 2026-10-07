using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Audit-redaction contract for <see cref="SystemSettings"/>: writes to the
/// encrypted secret columns (the SMTP password and the off-site storage
/// access/secret keys) produce an audit row whose snapshot stores a fixed
/// sentinel rather than the encrypted blob, so the audit log never carries
/// ciphertext history. See #485.
/// </summary>
public sealed class SystemSettingsAuditTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Save_writes_audit_row_with_redacted_smtp_password()
    {
        var protector = _db.DataProtectionProvider;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(NewInput("first.example.com", "first-secret"));
        }
        // Second save: triggers an Updated audit row whose snapshot
        // captures the *previous* row state (which holds ciphertext for
        // first-secret).
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(NewInput("second.example.com", "second-secret"));
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.SystemSettings)
            .ToListAsync();
        rows.Should().NotBeEmpty();
        // No snapshot should ever contain the plaintext password.
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain("first-secret");
            row.SnapshotJson.Should().NotContain("second-secret");
        }
        // At least one snapshot captured a non-null password column — the
        // second save's "before" view — and that must read "[redacted]"
        // rather than the ciphertext.
        rows.Where(r => r.SnapshotJson is not null && r.SnapshotJson.Contains("[redacted]"))
            .Should().NotBeEmpty(
                "the SMTP password column is replaced with a fixed sentinel before snapshotting");
    }

    [Fact]
    public async Task SaveOffsite_writes_audit_row_with_redacted_access_and_secret_keys()
    {
        var protector = _db.DataProtectionProvider;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveOffsiteAsync(NewOffsiteInput("first-access", "first-secret"));
        }
        // Second save: triggers an Updated audit row whose snapshot captures the
        // *previous* row state (which holds ciphertext for the first key pair).
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveOffsiteAsync(NewOffsiteInput("second-access", "second-secret"));
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.SystemSettings)
            .ToListAsync();
        rows.Should().NotBeEmpty();
        // No snapshot should ever contain the plaintext keys.
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain("first-access");
            row.SnapshotJson.Should().NotContain("first-secret");
            row.SnapshotJson.Should().NotContain("second-access");
            row.SnapshotJson.Should().NotContain("second-secret");
        }
        // The second save's "before" view held non-null ciphertext for both keys;
        // each must read "[redacted]" rather than the encrypted blob.
        rows.Where(r => r.SnapshotJson is not null && r.SnapshotJson.Contains("[redacted]"))
            .Should().NotBeEmpty(
                "the off-site key columns are replaced with a fixed sentinel before snapshotting");
    }

    [Fact]
    public async Task SaveGitHubApp_writes_audit_row_with_a_redacted_webhook_secret()
    {
        // The webhook secret is the one new key-ring-encrypted secret of #627, so
        // it has to be redacted the same way its siblings on this row are - an
        // audit trail carrying ciphertext history would defeat encrypting it.
        var protector = _db.DataProtectionProvider;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveGitHubAppAsync(NewGitHubInput("first-webhook-secret"));
        }
        // The second save's audit row snapshots the previous state, which is where
        // the first save's ciphertext would otherwise show up.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveGitHubAppAsync(NewGitHubInput("second-webhook-secret"));
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.SystemSettings)
            .ToListAsync();
        rows.Should().NotBeEmpty();
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain("first-webhook-secret");
            row.SnapshotJson.Should().NotContain("second-webhook-secret");
        }

        await using var reread = _db.NewContext();
        var stored = await reread.SystemSettings.AsNoTracking().FirstAsync(x => x.Id == 1);
        stored.GitHubWebhookSecretEncrypted.Should().NotBeNullOrEmpty();
        rows.Where(r => r.SnapshotJson is not null && r.SnapshotJson.Contains("[redacted]"))
            .Should().NotBeEmpty(
                "the webhook secret column is replaced with a fixed sentinel before snapshotting");
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain(stored.GitHubWebhookSecretEncrypted!,
                "not even the ciphertext belongs in the audit log");
        }
    }

    private static GitHubAppInput NewGitHubInput(string webhookSecret) => new(
        AppId: "123456",
        AppSlug: "al-workbench",
        ClientId: null,
        ClientSecret: null,
        ClearClientSecret: false,
        PrivateKeyPem: null,
        ClearPrivateKey: false,
        WebhookSecret: webhookSecret,
        ClearWebhookSecret: false);

    private static OffsiteSettingsInput NewOffsiteInput(string accessKey, string secretKey) => new(
        Enabled: true,
        Provider: null,
        Endpoint: null,
        Region: "eu-west-1",
        Bucket: "cronus-backups",
        Prefix: null,
        AccessKey: accessKey,
        ClearAccessKey: false,
        SecretKey: secretKey,
        ClearSecretKey: false,
        ForcePathStyle: false,
        RetentionDays: 30);

    private static SystemSettingsInput NewInput(string host, string password) => new(
        SmtpHost: host,
        SmtpPort: 587,
        SmtpUser: "u",
        SmtpPassword: password,
        ClearSmtpPassword: false,
        SmtpFrom: "noreply@example.com",
        SmtpFromName: null,
        SmtpUseStartTls: true,
        BannerText: null,
        BackupScheduleEnabled: true,
        BackupScheduleTimeUtc: new TimeOnly(2, 0),
        BackupRetentionCount: 14,
        PerTenantBackupRetentionCount: 30,
        DefaultStorageQuotaMb: null,
        IndexSizeMultiplier: 0.5m,
        McpEnabled: false,
        SignupEmailDomainAllowlist: null,
        ReleaseDownloadDomainAllowlist: null, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
        BuildConcurrency: null);
}
