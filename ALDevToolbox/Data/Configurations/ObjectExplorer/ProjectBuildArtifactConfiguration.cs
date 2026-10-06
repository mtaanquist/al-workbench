using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildArtifactConfiguration : IEntityTypeConfiguration<OeProjectBuildArtifact>
{
    public void Configure(EntityTypeBuilder<OeProjectBuildArtifact> entity)
    {
        entity.ToTable("oe_project_build_artifacts");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(400).IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(36);
        entity.Property(e => e.AppName).HasColumnName("app_name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.AppVersion).HasColumnName("app_version").HasMaxLength(50).IsRequired();
        entity.Property(e => e.RuntimeVersion).HasColumnName("runtime_version").HasMaxLength(50);
        entity.Property(e => e.SizeBytes).HasColumnName("size_bytes").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.CarriedFromBuildId).HasColumnName("carried_from_build_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The build relationship is configured from ProjectBuild
        // (HasMany(e => e.Artifacts)); don't redeclare it.

        entity.HasIndex(e => e.ProjectBuildId).HasDatabaseName("ix_oe_project_build_artifacts_build");
        // A build looks up the dependencies it does not compile itself by app id
        // across the organisation's earlier builds (#901, Part 3).
        entity.HasIndex(e => e.AppId).HasDatabaseName("ix_oe_project_build_artifacts_app_id");
    }
}
