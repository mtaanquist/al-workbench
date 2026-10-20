using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectEnvironmentConfiguration : IEntityTypeConfiguration<OeProjectEnvironment>
{
    public void Configure(EntityTypeBuilder<OeProjectEnvironment> entity)
    {
        entity.ToTable("oe_project_environments");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
        entity.Property(e => e.FetchedAt).HasColumnName("fetched_at").IsRequired();
        entity.Property(e => e.MissingSince).HasColumnName("missing_since");
        entity.Property(e => e.UpdateWindowStart).HasColumnName("update_window_start");
        entity.Property(e => e.UpdateWindowEnd).HasColumnName("update_window_end");
        entity.Property(e => e.UpdateWindowDays).HasColumnName("update_window_days");
        entity.Property(e => e.BcUpdateWindowStart).HasColumnName("bc_update_window_start");
        entity.Property(e => e.BcUpdateWindowEnd).HasColumnName("bc_update_window_end");
        entity.Property(e => e.BcUpdateWindowTimeZoneId).HasColumnName("bc_update_window_time_zone_id").HasMaxLength(100);
        entity.Property(e => e.BcUpdateWindowTimeZoneIana).HasColumnName("bc_update_window_time_zone_iana").HasMaxLength(100);
        entity.Property(e => e.BcUpdateWindowFetchedAt).HasColumnName("bc_update_window_fetched_at");
        entity.Property(e => e.BcNextUpdateVersion).HasColumnName("bc_next_update_version").HasMaxLength(50);
        entity.Property(e => e.BcNextUpdateType).HasColumnName("bc_next_update_type").HasMaxLength(50);
        entity.Property(e => e.BcNextUpdateStatus).HasColumnName("bc_next_update_status").HasMaxLength(50);
        entity.Property(e => e.BcNextUpdateDate).HasColumnName("bc_next_update_date");
        entity.Property(e => e.BcNextUpdateLatestDate).HasColumnName("bc_next_update_latest_date");
        entity.Property(e => e.BcNextUpdateIgnoresWindow).HasColumnName("bc_next_update_ignores_window");
        entity.Property(e => e.BcNextUpdateFetchedAt).HasColumnName("bc_next_update_fetched_at");
        // Nullable text[]: null is "never read", which is a different fact from an empty
        // list ("nothing on offer").
        entity.Property(e => e.BcOfferedVersions).HasColumnName("bc_offered_versions").HasColumnType("text[]");
        entity.Property(e => e.BcDatabaseKb).HasColumnName("bc_database_kb");

        // Fetched detail from the Admin Center API — all nullable, all refreshed by a
        // Refresh, none of them user config. Lengths are generous because the values
        // are Microsoft's strings, kept verbatim.
        entity.Property(e => e.FriendlyName).HasColumnName("friendly_name").HasMaxLength(200);
        entity.Property(e => e.ApplicationFamily).HasColumnName("application_family").HasMaxLength(100);
        entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50);
        entity.Property(e => e.StatusFetchedAt).HasColumnName("status_fetched_at");
        entity.Property(e => e.CountryCode).HasColumnName("country_code").HasMaxLength(10);
        entity.Property(e => e.AadTenantId).HasColumnName("aad_tenant_id");
        entity.Property(e => e.WebClientLoginUrl).HasColumnName("web_client_login_url").HasMaxLength(500);
        entity.Property(e => e.LocationName).HasColumnName("location_name").HasMaxLength(100);
        entity.Property(e => e.GeoName).HasColumnName("geo_name").HasMaxLength(100);
        entity.Property(e => e.RingName).HasColumnName("ring_name").HasMaxLength(100);
        entity.Property(e => e.AppSourceAppsUpdateCadence).HasColumnName("app_source_apps_update_cadence").HasMaxLength(50);
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(50);
        entity.Property(e => e.GracePeriodStartDate).HasColumnName("grace_period_start_date");
        entity.Property(e => e.EnforcedUpdatePeriodStartDate).HasColumnName("enforced_update_period_start_date");
        entity.Property(e => e.SoftDeletedOn).HasColumnName("soft_deleted_on");
        entity.Property(e => e.HardDeletePendingOn).HasColumnName("hard_delete_pending_on");
        entity.Property(e => e.DeleteReason).HasColumnName("delete_reason").HasMaxLength(500);

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Project -> Environments: cascade so a deleted project takes its fetched
        // environments with it. The release-pipeline FK to this row is Restrict
        // (configured on the ReleasePipeline side) so a customer-deleted
        // environment is stamped MissingSince rather than removed while referenced.
        entity.HasOne(e => e.Project)
            .WithMany(p => p.Environments)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The refresh upsert matches on (project_id, name); a unique index keeps
        // it one row per environment per project and backs the lookup.
        entity.HasIndex(e => new { e.ProjectId, e.Name })
            .IsUnique()
            .HasDatabaseName("ix_oe_project_environments_project_name");
    }
}
