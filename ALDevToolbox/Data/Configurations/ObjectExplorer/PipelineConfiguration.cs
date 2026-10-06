using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class PipelineConfiguration : IEntityTypeConfiguration<OePipeline>
{
    public void Configure(EntityTypeBuilder<OePipeline> entity)
    {
        entity.ToTable("oe_pipelines");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.NameIsCustom).HasColumnName("name_is_custom").IsRequired();
        entity.Property(e => e.RequestedAppIdsJson).HasColumnName("requested_app_ids_json");
        entity.Property(e => e.GithubReleaseRepositoryId).HasColumnName("github_release_repository_id");
        entity.Property(e => e.Branch).HasColumnName("branch").HasMaxLength(255);
        entity.Property(e => e.PreviewCheck).HasColumnName("preview_check").HasDefaultValue(false).IsRequired();
        entity.Property(e => e.PreviewCheckByUserId).HasColumnName("preview_check_by_user_id");
        entity.Property(e => e.PreviewCheckBlocked).HasColumnName("preview_check_blocked").HasMaxLength(500);
        entity.Property(e => e.BuildOnPush).HasColumnName("build_on_push").HasDefaultValue(false).IsRequired();
        entity.Property(e => e.BuildOnPushByUserId).HasColumnName("build_on_push_by_user_id");
        entity.Property(e => e.BuildOnPushBlocked).HasColumnName("build_on_push_blocked").HasMaxLength(500);
        // No HasDefaultValue(true): with a CLR default of true, EF would take false for
        // "unset" and leave it to the database default, so turning numbering off on a new
        // pipeline would not stick. The migration's default only fills existing rows.
        entity.Property(e => e.AutoVersion).HasColumnName("auto_version").IsRequired();
        entity.Property(e => e.ChangedAppsOnly).HasColumnName("changed_apps_only").HasDefaultValue(false).IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Pipelines ride the project's lifecycle.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The creator. SET NULL on delete so removing a user doesn't cascade-delete
        // their pipelines — management comes from the project owner via ProjectAccess.
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Who the nightly preview check runs as. SET NULL on delete, which pauses
        // the check rather than removing the pipeline.
        entity.HasOne(e => e.PreviewCheckByUser)
            .WithMany()
            .HasForeignKey(e => e.PreviewCheckByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Who builds started by a push run as. SET NULL on delete, which pauses
        // building on push rather than removing the pipeline.
        entity.HasOne(e => e.BuildOnPushByUser)
            .WithMany()
            .HasForeignKey(e => e.BuildOnPushByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The build relationship is configured from the ProjectBuild side; don't
        // redeclare it here.

        // Where successful builds are published as GitHub Releases. SET NULL so
        // removing the repository from the solution turns publishing off rather than
        // taking the pipeline with it.
        entity.HasOne(e => e.GithubReleaseRepository)
            .WithMany()
            .HasForeignKey(e => e.GithubReleaseRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.ProjectId).HasDatabaseName("ix_oe_pipelines_project");

        // Per-project name uniqueness on active rows is a functional, case-insensitive
        // index (lower(name)) EF can't model — created via raw SQL in the migration,
        // mirroring ix_oe_projects_org_name_active. Intentionally not declared here.
    }
}
