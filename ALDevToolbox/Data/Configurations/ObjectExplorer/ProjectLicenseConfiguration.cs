using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectLicenseConfiguration : IEntityTypeConfiguration<OeProjectLicense>
{
    public void Configure(EntityTypeBuilder<OeProjectLicense> entity)
    {
        entity.ToTable("oe_project_licenses");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.ProjectEnvironmentId).HasColumnName("project_environment_id");
        entity.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.Size).HasColumnName("size").IsRequired();
        entity.Property(e => e.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        entity.Property(e => e.LicenceTarget).HasColumnName("licence_target").HasMaxLength(20).IsRequired();
        entity.Property(e => e.UploadedByUserId).HasColumnName("uploaded_by_user_id");
        entity.Property(e => e.UploadedAt).HasColumnName("uploaded_at").IsRequired();
        entity.Property(e => e.GatewayCommandId).HasColumnName("gateway_command_id");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Licences ride the Solution's lifecycle, like its deployments.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ProjectEnvironment)
            .WithMany()
            .HasForeignKey(e => e.ProjectEnvironmentId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.UploadedByUser)
            .WithMany()
            .HasForeignKey(e => e.UploadedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The environment page's "last upload" read, newest first; also covers the
        // environment foreign key's SET NULL.
        entity.HasIndex(e => new { e.ProjectEnvironmentId, e.UploadedAt })
            .HasDatabaseName("ix_oe_project_licenses_environment_uploaded");
        entity.HasIndex(e => e.ProjectId)
            .HasDatabaseName("ix_oe_project_licenses_project_id");
        entity.HasIndex(e => e.UploadedByUserId)
            .HasDatabaseName("ix_oe_project_licenses_uploaded_by_user_id");
    }
}
