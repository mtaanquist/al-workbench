using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class EnvironmentUpgradeActionConfiguration : IEntityTypeConfiguration<OeEnvironmentUpgradeAction>
{
    public void Configure(EntityTypeBuilder<OeEnvironmentUpgradeAction> entity)
    {
        entity.ToTable("oe_environment_upgrade_actions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.EnvironmentId).HasColumnName("environment_id").IsRequired();

        // Text, not an int: the column reads plainly in psql and a third kind or a new
        // status never renumbers the rows already written. Same choice as Visibility.
        entity.Property(e => e.Kind).HasColumnName("kind")
            .HasConversion<string>().HasMaxLength(40).IsRequired();
        entity.Property(e => e.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        // SelectVersion and a booked UpdateApp carry one; a Business Central version is
        // "29.2" or at most four numeric segments, so 32 is generous.
        entity.Property(e => e.TargetVersion).HasColumnName("target_version").HasMaxLength(32);

        entity.Property(e => e.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(e => e.RequestedBy).HasColumnName("requested_by").HasMaxLength(320).IsRequired();
        entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
        entity.Property(e => e.ExecuteAfter).HasColumnName("execute_after").IsRequired();
        entity.Property(e => e.SentAt).HasColumnName("sent_at");
        entity.Property(e => e.Outcome).HasColumnName("outcome");
        entity.Property(e => e.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by").HasMaxLength(320);
        entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");

        // A booked upload's package, held until the row settles (see the entity's
        // remarks). bytea rather than a side table: at most one 50 MB file per booking,
        // read once by the worker and cleared in the same write that records the outcome.
        entity.Property(e => e.PackageFileName).HasColumnName("package_file_name").HasMaxLength(260);
        entity.Property(e => e.PackageContent).HasColumnName("package_content");
        // Several apps uploaded together: the worker runs a batch's rows in order.
        entity.Property(e => e.BatchId).HasColumnName("package_batch_id");
        entity.Property(e => e.BatchOrder).HasColumnName("package_batch_order");
        // What Business Central answered the upload with, so a restart mid-install knows
        // the app is already with it.
        entity.Property(e => e.BcAppId).HasColumnName("package_bc_app_id");
        entity.Property(e => e.BcOperationId).HasColumnName("package_bc_operation_id");
        // A booked AppSource update (#1001): the app's name for the lists, and the
        // prerequisites the person agreed to, which the worker holds Business Central to.
        entity.Property(e => e.AppName).HasColumnName("app_name").HasMaxLength(250);
        entity.Property(e => e.PrerequisiteAppIds).HasColumnName("update_prerequisite_app_ids").HasColumnType("uuid[]");

        // No concurrency-token column: see the entity's remarks. The one race on this
        // table — the worker claiming a row while somebody cancels it — is decided by a
        // conditional ExecuteUpdate, the pattern the rest of the codebase already uses.

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The history rides the customer's lifecycle: deleting the project for good
        // takes its upgrade history with it, like builds and deliveries.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Environment)
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both actors are SetNull: the feed must still say what happened after the
        // person who did it leaves, which is what the denormalised names are for.
        entity.HasOne(e => e.RequestedByUser)
            .WithMany()
            .HasForeignKey(e => e.RequestedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.CancelledByUser)
            .WithMany()
            .HasForeignKey(e => e.CancelledByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The planned upgrade the action was run from (#984); null for an ad hoc action.
        // SetNull so the history outlives the upgrade, though an upgrade that has sent
        // anything cannot be deleted in the first place.
        entity.Property(e => e.UpgradeId).HasColumnName("upgrade_id");
        entity.HasOne(e => e.Upgrade)
            .WithMany()
            .HasForeignKey(e => e.UpgradeId)
            .OnDelete(DeleteBehavior.SetNull);
        // Read by every derived line state, and the index behind the SetNull.
        entity.HasIndex(e => e.UpgradeId)
            .HasDatabaseName("ix_oe_env_upgrade_actions_upgrade");

        // The activity feed: one environment's actions, newest first.
        entity.HasIndex(e => new { e.EnvironmentId, e.RequestedAt })
            .HasDatabaseName("ix_oe_env_upgrade_actions_env_requested");
        // The worker's due-scan (status-scoped).
        entity.HasIndex(e => new { e.Status, e.ExecuteAfter })
            .HasDatabaseName("ix_oe_env_upgrade_actions_status_due");
    }
}
