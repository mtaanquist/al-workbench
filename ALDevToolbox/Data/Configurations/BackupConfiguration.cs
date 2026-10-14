using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class BackupConfiguration : IEntityTypeConfiguration<Backup>
{
    public void Configure(EntityTypeBuilder<Backup> entity)
    {
        entity.ToTable("backups");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
        entity.Property(e => e.IsPinned).HasColumnName("is_pinned").IsRequired();
        entity.Property(e => e.OffsiteUploadedAt).HasColumnName("offsite_uploaded_at");
        entity.Property(e => e.OffsiteObjectKey).HasColumnName("offsite_object_key");
        entity.Property(e => e.RecoveredFromDeploymentId).HasColumnName("recovered_from_deployment_id").HasMaxLength(128);
        entity.HasIndex(e => e.FileName).IsUnique();
        entity.HasIndex(e => e.CreatedAt);
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        // Cross-org table — SiteAdminService gates mutations.
    }
}
