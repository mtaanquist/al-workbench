using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ModuleConfiguration : IEntityTypeConfiguration<Module>
{

    public void Configure(EntityTypeBuilder<Module> entity)
    {
        entity.ToTable("modules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Key).HasColumnName("key").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Key }).IsUnique();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.ExtensionName).HasColumnName("extension_name").IsRequired();
        entity.Property(e => e.IdRangeSize).HasColumnName("id_range_size");
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        // Repository source (#1415). All null for a typed module.
        entity.Property(e => e.SourceRepository).HasColumnName("source_repository").HasMaxLength(200);
        entity.Property(e => e.SourceRef).HasColumnName("source_ref").HasMaxLength(255);
        entity.Property(e => e.SourcePath).HasColumnName("source_path").HasMaxLength(500);
        entity.Property(e => e.SourceCommitSha).HasColumnName("source_commit_sha").HasMaxLength(64);
        entity.Property(e => e.SourceFetchedAt).HasColumnName("source_fetched_at");
        entity.Property(e => e.SourceCheckedAt).HasColumnName("source_checked_at");
        entity.Property(e => e.SourceError).HasColumnName("source_error");
        entity.Property(e => e.SourceSkippedJson).HasColumnName("source_skipped_json").HasColumnType("jsonb");
        entity.Property(e => e.SourceDependenciesJson).HasColumnName("source_dependencies_json").HasColumnType("jsonb");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Dependencies)
            .WithOne(d => d.Module!)
            .HasForeignKey(d => d.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.ExtensionFolders)
            .WithOne(f => f.Module!)
            .HasForeignKey(f => f.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
