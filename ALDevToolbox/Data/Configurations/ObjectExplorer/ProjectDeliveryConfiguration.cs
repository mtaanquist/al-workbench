using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectDeliveryConfiguration : IEntityTypeConfiguration<OeProjectDelivery>
{
    public void Configure(EntityTypeBuilder<OeProjectDelivery> entity)
    {
        entity.ToTable("oe_project_deliveries");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.ReleasePipelineId).HasColumnName("release_pipeline_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.TriggeredByUserId).HasColumnName("triggered_by_user_id");

        entity.Property(e => e.EnvironmentName).HasColumnName("environment_name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.DeploymentSchedule).HasColumnName("deployment_schedule").HasMaxLength(50).IsRequired();
        entity.Property(e => e.SchemaSyncMode).HasColumnName("schema_sync_mode").HasMaxLength(50).IsRequired();

        entity.Property(e => e.ScheduledFor).HasColumnName("scheduled_for").IsRequired();
        entity.Property(e => e.ScheduledOutsideWindow).HasColumnName("scheduled_outside_window").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.ScheduledByDeliveryWindow).HasColumnName("scheduled_by_delivery_window").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.ClaimedAt).HasColumnName("claimed_at");
        entity.Property(e => e.StartedAt).HasColumnName("started_at");
        entity.Property(e => e.InstallStartedAt).HasColumnName("install_started_at");
        entity.Property(e => e.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
        entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        entity.Property(e => e.DismissReason).HasColumnName("dismiss_reason");
        entity.Property(e => e.ReplacedByProjectBuildId).HasColumnName("replaced_by_project_build_id");
        entity.Property(e => e.DeployedWithoutApproval).HasColumnName("deployed_without_approval").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.StartedByAgent).HasColumnName("started_by_agent").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.FailureMessage).HasColumnName("failure_message");
        entity.Property(e => e.DiagnosticsLog).HasColumnName("diagnostics_log");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deliveries ride the project's lifecycle (cascade), like builds.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade from the release pipeline: a (soft-deleted) release pipeline keeps
        // its deliveries, but a hard project-cascade removes the lot together.
        entity.HasOne(e => e.ReleasePipeline)
            .WithMany()
            .HasForeignKey(e => e.ReleasePipelineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict the build: a build that's been released stays around for the
        // delivery's history rather than vanishing under it.
        entity.HasOne(e => e.ProjectBuild)
            .WithMany()
            .HasForeignKey(e => e.ProjectBuildId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.TriggeredByUser)
            .WithMany()
            .HasForeignKey(e => e.TriggeredByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.CancelledByUser)
            .WithMany()
            .HasForeignKey(e => e.CancelledByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // History listings: a release pipeline's deliveries, newest first.
        entity.HasIndex(e => new { e.ReleasePipelineId, e.CreatedAt })
            .HasDatabaseName("ix_oe_project_deliveries_pipeline_created");
        // The worker's due-scan / claim path (status-scoped).
        entity.HasIndex(e => new { e.Status, e.ScheduledFor })
            .HasDatabaseName("ix_oe_project_deliveries_status_scheduled");
    }
}
