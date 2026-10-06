using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ReleasePipelineConfiguration : IEntityTypeConfiguration<OeReleasePipeline>
{
    public void Configure(EntityTypeBuilder<OeReleasePipeline> entity)
    {
        entity.ToTable("oe_release_pipelines");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.NameIsCustom).HasColumnName("name_is_custom").IsRequired();
        entity.Property(e => e.ArtifactSource).HasColumnName("artifact_source").HasMaxLength(30).IsRequired();
        entity.Property(e => e.BuildPipelineId).HasColumnName("build_pipeline_id");
        entity.Property(e => e.GithubReleaseRepositoryId).HasColumnName("github_release_repository_id");
        entity.Property(e => e.ProjectEnvironmentId).HasColumnName("project_environment_id").IsRequired();
        entity.Property(e => e.DeploymentSchedule).HasColumnName("deployment_schedule").HasMaxLength(50).IsRequired();
        entity.Property(e => e.SchemaSyncMode).HasColumnName("schema_sync_mode").HasMaxLength(50).IsRequired();
        entity.Property(e => e.PrepareReleaseOnNewBuild).HasColumnName("prepare_release_on_new_build").IsRequired();
        entity.Property(e => e.DeployWithoutApproval).HasColumnName("deploy_without_approval").HasDefaultValue(false).IsRequired();
        entity.Property(e => e.DeployWithoutApprovalByUserId).HasColumnName("deploy_without_approval_by_user_id");
        entity.Property(e => e.RestrictBranch).HasColumnName("restrict_branch").IsRequired();
        entity.Property(e => e.AllowedBranch).HasColumnName("allowed_branch").HasMaxLength(255);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Project -> ReleasePipelines: cascade so a deleted project takes its
        // release configs with it (matching Pipeline).
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The owner of record survives the account being deleted.
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Whoever turned on deploying without approval; their deletion pauses it.
        entity.HasOne(e => e.DeployWithoutApprovalByUser)
            .WithMany()
            .HasForeignKey(e => e.DeployWithoutApprovalByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Build pipeline is the artifact source. Restrict: don't let a build
        // pipeline be hard-deleted out from under a release pipeline that draws
        // from it (build pipelines are soft-deleted anyway).
        entity.HasOne(e => e.BuildPipeline)
            .WithMany()
            .HasForeignKey(e => e.BuildPipelineId)
            .OnDelete(DeleteBehavior.Restrict);

        // Target environment. Restrict so a customer-deleted environment is
        // stamped MissingSince (see ProjectEnvironment) rather than removed while
        // a release pipeline still points at it.
        entity.HasOne(e => e.ProjectEnvironment)
            .WithMany()
            .HasForeignKey(e => e.ProjectEnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // The other artifact source: a repository's GitHub Releases. SET NULL so
        // removing the repository from the solution leaves the pipeline's delivery
        // history readable rather than deleting it.
        entity.HasOne(e => e.GithubReleaseRepository)
            .WithMany()
            .HasForeignKey(e => e.GithubReleaseRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.ProjectId).HasDatabaseName("ix_oe_release_pipelines_project");
        entity.HasIndex(e => e.BuildPipelineId)
            .HasDatabaseName("ix_oe_release_pipelines_build_pipeline");
        entity.HasIndex(e => e.ProjectEnvironmentId)
            .HasDatabaseName("ix_oe_release_pipelines_environment");

        // Per-project name uniqueness on active rows is a functional, case-insensitive
        // index (lower(name)) EF can't model — created via raw SQL in the migration,
        // mirroring ix_oe_pipelines / ix_oe_projects_org_name_active. Not declared here.
    }
}
