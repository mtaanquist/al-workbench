using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubRepositoryFileCheckConfiguration : IEntityTypeConfiguration<GitHubRepositoryFileCheck>
{
    public void Configure(EntityTypeBuilder<GitHubRepositoryFileCheck> entity)
    {
        entity.ToTable("github_repository_file_checks");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300).IsRequired();
        entity.PrimitiveCollection(e => e.MissingPaths).HasColumnName("missing_paths").IsRequired();
        entity.PrimitiveCollection(e => e.ChangedPaths).HasColumnName("changed_paths").IsRequired();
        entity.Property(e => e.PullRequestNumber).HasColumnName("pull_request_number");
        entity.Property(e => e.PullRequestUrl).HasColumnName("pull_request_url").HasMaxLength(500);
        entity.Property(e => e.CheckedAt).HasColumnName("checked_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per repository per organisation: a check replaces it.
        entity.HasIndex(e => new { e.OrganizationId, e.Repository })
            .IsUnique()
            .HasDatabaseName("ux_github_repository_file_checks_org_repo");
    }
}
