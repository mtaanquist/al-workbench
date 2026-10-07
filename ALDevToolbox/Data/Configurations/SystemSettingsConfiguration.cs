using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SystemSettingsConfiguration : IEntityTypeConfiguration<SystemSettings>
{
    public void Configure(EntityTypeBuilder<SystemSettings> entity)
    {
        // CHECK (id = 1) makes the documented singleton invariant self-enforcing
        // at the DB level, not just by the convention that SystemSettingsService
        // is the only writer — a second row can't be inserted. #402
        entity.ToTable("system_settings", t =>
            t.HasCheckConstraint("ck_system_settings_singleton", "id = 1"));
        entity.HasKey(e => e.Id);
        // The singleton row's id is pinned to 1 by the migration.
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(e => e.SmtpHost).HasColumnName("smtp_host");
        entity.Property(e => e.SmtpPort).HasColumnName("smtp_port");
        entity.Property(e => e.SmtpUser).HasColumnName("smtp_user");
        entity.Property(e => e.SmtpPasswordEncrypted).HasColumnName("smtp_password_encrypted");
        entity.Property(e => e.SmtpFrom).HasColumnName("smtp_from");
        entity.Property(e => e.SmtpFromName).HasColumnName("smtp_from_name");
        entity.Property(e => e.SmtpUseStartTls).HasColumnName("smtp_use_starttls");
        entity.Property(e => e.BannerText).HasColumnName("banner_text");
        entity.Property(e => e.BackupScheduleEnabled).HasColumnName("backup_schedule_enabled").IsRequired();
        entity.Property(e => e.BackupScheduleTimeUtc).HasColumnName("backup_schedule_time_utc")
            .HasColumnType("time without time zone").IsRequired();
        entity.Property(e => e.BackupRetentionCount).HasColumnName("backup_retention_count").IsRequired();
        entity.Property(e => e.PerTenantBackupRetentionCount).HasColumnName("per_tenant_backup_retention_count").IsRequired();
        entity.Property(e => e.DefaultStorageQuotaMb).HasColumnName("default_storage_quota_mb");
        entity.Property(e => e.IndexSizeMultiplier).HasColumnName("index_size_multiplier")
            .HasColumnType("numeric(6,3)").IsRequired();
        entity.Property(e => e.OffsiteBackupEnabled).HasColumnName("offsite_backup_enabled").IsRequired();
        entity.Property(e => e.OffsiteEndpoint).HasColumnName("offsite_endpoint");
        entity.Property(e => e.OffsiteRegion).HasColumnName("offsite_region");
        entity.Property(e => e.OffsiteBucket).HasColumnName("offsite_bucket");
        entity.Property(e => e.OffsitePrefix).HasColumnName("offsite_prefix");
        entity.Property(e => e.OffsiteAccessKeyEncrypted).HasColumnName("offsite_access_key_encrypted");
        entity.Property(e => e.OffsiteSecretKeyEncrypted).HasColumnName("offsite_secret_key_encrypted");
        entity.Property(e => e.OffsiteForcePathStyle).HasColumnName("offsite_force_path_style").IsRequired();
        entity.Property(e => e.OffsiteRetentionDays).HasColumnName("offsite_retention_days").IsRequired();
        entity.Property(e => e.OffsiteProvider).HasColumnName("offsite_provider").IsRequired();
        entity.Property(e => e.McpEnabled).HasColumnName("mcp_enabled").IsRequired();
        entity.Property(e => e.BuildConcurrency).HasColumnName("build_concurrency");
        // text[] of ToolKey names; empty-array default backfills the singleton
        // row so every tool stays on after the migration. See Domain/Tools/ToolCatalog.cs.
        entity.Property(e => e.DisabledTools)
            .HasColumnName("disabled_tools")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();
        entity.Property(e => e.EntraClientId).HasColumnName("entra_client_id").HasMaxLength(64);
        entity.Property(e => e.EntraClientSecretEncrypted).HasColumnName("entra_client_secret_encrypted");
        // Deployment-wide GitHub App registration. The two *_encrypted columns
        // hold Data-Protection ciphertext and are redacted by AuditInterceptor.
        entity.Property(e => e.GitHubAppId).HasColumnName("github_app_id");
        entity.Property(e => e.GitHubAppSlug).HasColumnName("github_app_slug").HasMaxLength(120);
        entity.Property(e => e.GitHubClientId).HasColumnName("github_client_id").HasMaxLength(120);
        entity.Property(e => e.GitHubClientSecretEncrypted).HasColumnName("github_client_secret_encrypted");
        entity.Property(e => e.GitHubPrivateKeyEncrypted).HasColumnName("github_private_key_encrypted");
        entity.Property(e => e.GitHubWebhookSecretEncrypted).HasColumnName("github_webhook_secret_encrypted");
        entity.Property(e => e.SignupEmailDomainAllowlist).HasColumnName("signup_email_domain_allowlist");
        entity.Property(e => e.ReleaseDownloadDomainAllowlist).HasColumnName("release_download_domain_allowlist");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        // Cross-org table: no organization_id and no scoping query filter;
        // SiteAdminService gates mutations.
    }
}
