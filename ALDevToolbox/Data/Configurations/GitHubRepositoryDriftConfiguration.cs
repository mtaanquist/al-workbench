using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubRepositoryDriftConfiguration : IEntityTypeConfiguration<GitHubRepositoryDrift>
{
    public void Configure(EntityTypeBuilder<GitHubRepositoryDrift> entity)
    {
        entity.ToTable("github_repository_drift");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300).IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").HasMaxLength(500).IsRequired();
        entity.Property(e => e.Field).HasColumnName("field").HasMaxLength(120).IsRequired();
        entity.Property(e => e.Current).HasColumnName("current").HasMaxLength(100).IsRequired();
        entity.Property(e => e.Proposed).HasColumnName("proposed").HasMaxLength(100).IsRequired();
        entity.Property(e => e.ReleaseId).HasColumnName("release_id").IsRequired();
        entity.Property(e => e.EnvironmentId).HasColumnName("environment_id");
        entity.Property(e => e.DetectedAt).HasColumnName("detected_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The finding belongs to the release that turned it up: deleting the
        // release deletes what it said about repositories, rather than leaving
        // proposals nothing can explain any more.
        entity.HasOne(e => e.Release)
            .WithMany()
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // A finding measured against a production environment goes with it. The
        // index covers the cascade: an environment delete must not scan the table.
        entity.HasOne(e => e.Environment)
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(e => e.EnvironmentId)
            .HasDatabaseName("ix_github_repository_drift_environment_id");

        // One finding per value per manifest per organisation, so a rescan
        // replaces what it found rather than piling a second copy beside it.
        entity.HasIndex(e => new { e.OrganizationId, e.Repository, e.Path, e.Field })
            .IsUnique()
            .HasDatabaseName("ux_github_repository_drift_org_repo_path_field");
    }
}
