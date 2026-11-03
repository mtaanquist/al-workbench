using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RepositoryMigrationConfiguration : IEntityTypeConfiguration<RepositoryMigration>
{
    public void Configure(EntityTypeBuilder<RepositoryMigration> entity)
    {
        entity.ToTable("repository_migrations");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.SourceProject).HasColumnName("source_project").HasMaxLength(200).IsRequired();
        entity.Property(e => e.SourceProjectUrl).HasColumnName("source_project_url").HasMaxLength(500).IsRequired();
        entity.Property(e => e.Sources).HasColumnName("sources").IsRequired();
        entity.Property(e => e.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.TemplateKey).HasColumnName("template_key").HasMaxLength(200).IsRequired();
        entity.Property(e => e.RepositoryFullName).HasColumnName("repository_full_name").HasMaxLength(300);
        entity.Property(e => e.RepositoryUrl).HasColumnName("repository_url").HasMaxLength(500);
        entity.Property(e => e.ProjectId).HasColumnName("project_id");
        entity.Property(e => e.Succeeded).HasColumnName("succeeded").IsRequired();
        entity.Property(e => e.Message).HasColumnName("message");

        // The page's list, newest first.
        entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt });
        // Covers the foreign key's SET NULL when a solution is deleted, and the
        // "did this solution come from a migration" lookup.
        entity.HasIndex(e => e.ProjectId);
        entity.HasIndex(e => e.CreatedByUserId);

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // Query filter installed in AppDbContext.OnModelCreating.
    }
}
