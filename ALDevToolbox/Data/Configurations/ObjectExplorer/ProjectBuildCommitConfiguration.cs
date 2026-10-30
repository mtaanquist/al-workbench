using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildCommitConfiguration : IEntityTypeConfiguration<OeProjectBuildCommit>
{
    public void Configure(EntityTypeBuilder<OeProjectBuildCommit> entity)
    {
        entity.ToTable("oe_project_build_commits");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id");
        entity.Property(e => e.ShortHash).HasColumnName("short_hash").HasMaxLength(64).IsRequired();
        entity.Property(e => e.Message).HasColumnName("message").IsRequired();
        entity.Property(e => e.Author).HasColumnName("author").HasMaxLength(250).IsRequired();
        entity.Property(e => e.AuthorEmail).HasColumnName("author_email").HasMaxLength(320);
        entity.Property(e => e.CommittedAt).HasColumnName("committed_at");
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The build relationship is configured from ProjectBuild
        // (HasMany(e => e.Changelog)); don't redeclare it.

        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.ProjectBuildId).HasDatabaseName("ix_oe_project_build_commits_build");
    }
}
