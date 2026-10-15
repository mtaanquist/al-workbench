using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

/// <summary>The on-premises mirror (#1365). See <see cref="GatewayInstance"/>.</summary>
internal sealed class GatewayInstanceConfiguration : IEntityTypeConfiguration<GatewayInstance>
{
    public void Configure(EntityTypeBuilder<GatewayInstance> entity)
    {
        entity.ToTable("gateway_instances");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.GatewayId).HasColumnName("gateway_id").IsRequired();
        entity.Property(e => e.Machine).HasColumnName("machine").HasMaxLength(255).IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        entity.Property(e => e.Environment).HasColumnName("environment").HasMaxLength(20).IsRequired();
        entity.Property(e => e.State).HasColumnName("state");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.Property(e => e.ServiceState).HasColumnName("service_state");
        entity.Property(e => e.ServiceStartMode).HasColumnName("service_start_mode");
        entity.Property(e => e.ServiceAccount).HasColumnName("service_account");
        entity.Property(e => e.DatabaseServer).HasColumnName("database_server");
        entity.Property(e => e.DatabaseInstance).HasColumnName("database_instance");
        entity.Property(e => e.DatabaseName).HasColumnName("database_name");
        entity.Property(e => e.Multitenant).HasColumnName("multitenant");
        entity.Property(e => e.ClientServicesPort).HasColumnName("client_services_port");
        entity.Property(e => e.SoapServicesPort).HasColumnName("soap_services_port");
        entity.Property(e => e.ODataServicesPort).HasColumnName("odata_services_port");
        entity.Property(e => e.ManagementServicesPort).HasColumnName("management_services_port");
        entity.Property(e => e.DeveloperServicesPort).HasColumnName("developer_services_port");
        entity.Property(e => e.PublicWebBaseUrl).HasColumnName("public_web_base_url");
        entity.Property(e => e.SessionCount).HasColumnName("session_count");
        entity.Property(e => e.SessionUsersJson).HasColumnName("session_users").HasColumnType("jsonb");
        entity.Property(e => e.PowerShell).HasColumnName("powershell").HasMaxLength(10);
        entity.Property(e => e.ReadError).HasColumnName("read_error");
        entity.Property(e => e.UnavailableJson).HasColumnName("unavailable").HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.TenantsTruncated).HasColumnName("tenants_truncated").IsRequired();
        entity.Property(e => e.FirstReportedAt).HasColumnName("first_reported_at").IsRequired();
        entity.Property(e => e.ReportedAt).HasColumnName("reported_at").IsRequired();
        entity.Property(e => e.ReadAt).HasColumnName("read_at");
        entity.Property(e => e.MissingSince).HasColumnName("missing_since");

        // The upsert key. Machine and instance names compare without regard to case
        // (as Windows does) in GatewayInventoryService, which finds the existing row
        // before inserting; the index keeps two rows from ever sharing a spelling.
        entity.HasIndex(e => new { e.GatewayId, e.Machine, e.Name }).IsUnique();

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Gateway)
            .WithMany()
            .HasForeignKey(e => e.GatewayId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Tenants)
            .WithOne(t => t.Instance)
            .HasForeignKey(t => t.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Licence)
            .WithOne(l => l.Instance)
            .HasForeignKey<GatewayInstanceLicence>(l => l.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GatewayInstanceTenantConfiguration : IEntityTypeConfiguration<GatewayInstanceTenant>
{
    public void Configure(EntityTypeBuilder<GatewayInstanceTenant> entity)
    {
        entity.ToTable("gateway_instance_tenants");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.InstanceId).HasColumnName("instance_id").IsRequired();
        entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(255).IsRequired();
        entity.Property(e => e.State).HasColumnName("state");
        entity.Property(e => e.DatabaseName).HasColumnName("database_name");
        entity.Property(e => e.SessionCount).HasColumnName("session_count");
        entity.Property(e => e.AppsTruncated).HasColumnName("apps_truncated").IsRequired();
        entity.Property(e => e.AppsReadAt).HasColumnName("apps_read_at");
        entity.Property(e => e.ReadAt).HasColumnName("read_at").IsRequired();
        entity.Property(e => e.MissingSince).HasColumnName("missing_since");

        entity.HasIndex(e => new { e.InstanceId, e.TenantId }).IsUnique();

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Apps)
            .WithOne(a => a.Tenant)
            .HasForeignKey(a => a.TenantRowId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GatewayInstanceAppConfiguration : IEntityTypeConfiguration<GatewayInstanceApp>
{
    public void Configure(EntityTypeBuilder<GatewayInstanceApp> entity)
    {
        entity.ToTable("gateway_instance_apps");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.TenantRowId).HasColumnName("tenant_row_id").IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(64).IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Publisher).HasColumnName("publisher").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
        entity.Property(e => e.DataVersion).HasColumnName("data_version").HasMaxLength(64);
        entity.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(20);
        entity.Property(e => e.SyncState).HasColumnName("sync_state").HasMaxLength(40);
        entity.Property(e => e.IsInstalled).HasColumnName("is_installed");
        entity.Property(e => e.IsPublished).HasColumnName("is_published");
        entity.Property(e => e.MissingSince).HasColumnName("missing_since");

        // Several versions of one app are published at once during an upgrade.
        entity.HasIndex(e => new { e.TenantRowId, e.AppId, e.Version }).IsUnique();

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GatewayInstanceLicenceConfiguration : IEntityTypeConfiguration<GatewayInstanceLicence>
{
    public void Configure(EntityTypeBuilder<GatewayInstanceLicence> entity)
    {
        entity.ToTable("gateway_instance_licences");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.InstanceId).HasColumnName("instance_id").IsRequired();
        entity.Property(e => e.Licensee).HasColumnName("licensee");
        entity.Property(e => e.SerialNumber).HasColumnName("serial_number");
        entity.Property(e => e.ExpiresOn).HasColumnName("expires_on");
        entity.Property(e => e.Text).HasColumnName("text").IsRequired();
        entity.Property(e => e.TextSha256).HasColumnName("text_sha256").HasMaxLength(64).IsRequired();
        entity.Property(e => e.Truncated).HasColumnName("truncated").IsRequired();
        entity.Property(e => e.ParseProblems).HasColumnName("parse_problems");
        entity.Property(e => e.ReadAt).HasColumnName("read_at").IsRequired();

        entity.HasIndex(e => e.InstanceId).IsUnique();

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Ranges)
            .WithOne(r => r.Licence)
            .HasForeignKey(r => r.LicenceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GatewayInstanceLicenceRangeConfiguration : IEntityTypeConfiguration<GatewayInstanceLicenceRange>
{
    public void Configure(EntityTypeBuilder<GatewayInstanceLicenceRange> entity)
    {
        entity.ToTable("gateway_instance_licence_ranges");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.LicenceId).HasColumnName("licence_id").IsRequired();
        entity.Property(e => e.ObjectType).HasColumnName("object_type").HasMaxLength(40).IsRequired();
        entity.Property(e => e.FromId).HasColumnName("from_id").IsRequired();
        entity.Property(e => e.ToId).HasColumnName("to_id").IsRequired();
        entity.Property(e => e.Permissions).HasColumnName("permissions").HasMaxLength(200);

        // The range check (#1370) asks "which ranges of this type does this licence have".
        entity.HasIndex(e => new { e.LicenceId, e.ObjectType });

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
