using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubRepositoryCheckConfiguration : IEntityTypeConfiguration<GitHubRepositoryCheck>
{
    public void Configure(EntityTypeBuilder<GitHubRepositoryCheck> entity)
    {
        entity.ToTable("github_repository_checks");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300).IsRequired();
        entity.Property(e => e.Application).HasColumnName("application").HasMaxLength(40);
        entity.Property(e => e.CheckedAt).HasColumnName("checked_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per repository per organisation: a rescan replaces it.
        entity.HasIndex(e => new { e.OrganizationId, e.Repository })
            .IsUnique()
            .HasDatabaseName("ux_github_repository_checks_org_repo");
    }
}
