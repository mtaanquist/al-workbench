using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildConfiguration : IEntityTypeConfiguration<OeProjectBuild>
{
    public void Configure(EntityTypeBuilder<OeProjectBuild> entity)
    {
        entity.ToTable("oe_project_builds");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.PipelineId).HasColumnName("pipeline_id");
        entity.Property(e => e.StartedByUserId).HasColumnName("started_by_user_id");
        entity.Property(e => e.ReleaseId).HasColumnName("release_id");
        entity.Property(e => e.Branch).HasColumnName("branch").HasMaxLength(250);
        entity.Property(e => e.DefaultBranch).HasColumnName("default_branch").HasMaxLength(250);
        entity.Property(e => e.Trigger).HasColumnName("trigger").HasMaxLength(20)
            .HasDefaultValue(ProjectBuildTrigger.Manual).IsRequired();
        entity.Property(e => e.PullRequestNumber).HasColumnName("pull_request_number");
        entity.Property(e => e.HeadSha).HasColumnName("head_sha").HasMaxLength(64);
        entity.Property(e => e.HeadRepositoryId).HasColumnName("head_repository_id");
        entity.Property(e => e.CheckRunId).HasColumnName("check_run_id");
        entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        entity.Property(e => e.BcVersion).HasColumnName("bc_version").HasMaxLength(50);
        entity.Property(e => e.BcArtifactVersion).HasColumnName("bc_artifact_version").HasMaxLength(50);
        entity.Property(e => e.BcTarget).HasColumnName("bc_target").HasMaxLength(ProjectBuildTarget.MaxLength)
            .HasDefaultValue(ProjectBuildTarget.Current).IsRequired();
        entity.Property(e => e.FailureMessage).HasColumnName("failure_message");
        entity.Property(e => e.RequestedAppIdsJson).HasColumnName("requested_app_ids_json");
        entity.Property(e => e.GithubReleaseTag).HasColumnName("github_release_tag").HasMaxLength(200);
        entity.Property(e => e.GithubReleaseUrl).HasColumnName("github_release_url").HasMaxLength(500);
        entity.Property(e => e.GithubReleaseError).HasColumnName("github_release_error").HasMaxLength(2000);
        entity.Property(e => e.StagedFromRepositoryId).HasColumnName("staged_from_repository_id");
        entity.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        entity.Property(e => e.BuildingStartedAt).HasColumnName("building_started_at");
        entity.Property(e => e.FinishedAt).HasColumnName("finished_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The project relationship is configured from the Project side
        // (ProjectConfiguration.HasMany(e => e.Builds)); don't redeclare it here.

        // The starter outlives nothing: removing the user nulls the pointer but
        // keeps the build's deliverables and history.
        entity.HasOne(e => e.StartedByUser)
            .WithMany()
            .HasForeignKey(e => e.StartedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The produced project Release — the Object Explorer hook. SET NULL so a
        // reaped release doesn't erase the build; its artifacts/logs remain.
        entity.HasOne(e => e.Release)
            .WithMany()
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.SetNull);

        // The pipeline this build is a run of. SET NULL so deleting a pipeline keeps
        // its build history reachable (the build stays attributable via project_id).
        entity.HasOne(e => e.Pipeline)
            .WithMany(p => p.Builds)
            .HasForeignKey(e => e.PipelineId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.RepoCommits)
            .WithOne(c => c.ProjectBuild!)
            .HasForeignKey(c => c.ProjectBuildId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Changelog)
            .WithOne(c => c.ProjectBuild!)
            .HasForeignKey(c => c.ProjectBuildId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Artifacts)
            .WithOne(a => a.ProjectBuild!)
            .HasForeignKey(a => a.ProjectBuildId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Logs)
            .WithOne(l => l.ProjectBuild!)
            .HasForeignKey(l => l.ProjectBuildId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Diagnostics)
            .WithOne(d => d.ProjectBuild!)
            .HasForeignKey(d => d.ProjectBuildId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Artifacts UI lists a project's builds newest-first, and the
        // changelog needs the project's last successful build.
        entity.HasIndex(e => new { e.ProjectId, e.StartedAt }).HasDatabaseName("ix_oe_project_builds_project_started");
        // A pipeline's builds, newest-first (the pipeline detail page's history list).
        entity.HasIndex(e => new { e.PipelineId, e.StartedAt }).HasDatabaseName("ix_oe_project_builds_pipeline_started");
        // The build that produced a given release (deep-link "back to artifact").
        entity.HasIndex(e => e.ReleaseId).HasDatabaseName("ix_oe_project_builds_release");
    }
}
