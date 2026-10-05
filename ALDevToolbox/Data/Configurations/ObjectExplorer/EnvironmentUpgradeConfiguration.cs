using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

/// <summary>
/// The header of a planned upgrade (issue #984). See
/// <c>.design/environment-updates.md</c>, "Planned upgrades: a header with lines".
/// </summary>
internal sealed class EnvironmentUpgradeConfiguration : IEntityTypeConfiguration<OeEnvironmentUpgrade>
{
    public void Configure(EntityTypeBuilder<OeEnvironmentUpgrade> entity)
    {
        entity.ToTable("oe_environment_upgrades");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.TargetVersion).HasColumnName("target_version").HasMaxLength(50).IsRequired();
        entity.Property(e => e.PlannedAt).HasColumnName("planned_at");
        entity.Property(e => e.Note).HasColumnName("note");
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(320).IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
        entity.Property(e => e.ClosedByUserId).HasColumnName("closed_by_user_id");
        entity.Property(e => e.ClosedBy).HasColumnName("closed_by").HasMaxLength(320);
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both actors are SetNull, like the action rows: the record still names them
        // after the account is gone, through the denormalised strings.
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.ClosedByUser)
            .WithMany()
            .HasForeignKey(e => e.ClosedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The open list and the archive are the two reads, split on closed_at.
        entity.HasIndex(e => new { e.OrganizationId, e.ClosedAt })
            .HasDatabaseName("ix_oe_environment_upgrades_org_closed");
    }
}

/// <summary>
/// One environment on a planned upgrade (issue #984).
/// </summary>
internal sealed class EnvironmentUpgradeLineConfiguration : IEntityTypeConfiguration<OeEnvironmentUpgradeLine>
{
    public void Configure(EntityTypeBuilder<OeEnvironmentUpgradeLine> entity)
    {
        entity.ToTable("oe_environment_upgrade_lines");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.UpgradeId).HasColumnName("upgrade_id").IsRequired();
        entity.Property(e => e.EnvironmentId).HasColumnName("environment_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.IsOpen).HasColumnName("is_open").IsRequired();
        entity.Property(e => e.AssigneeUserId).HasColumnName("assignee_user_id");
        entity.Property(e => e.CheckedAt).HasColumnName("checked_at");
        entity.Property(e => e.CheckedByUserId).HasColumnName("checked_by_user_id");
        entity.Property(e => e.CheckedBy).HasColumnName("checked_by").HasMaxLength(320);
        entity.Property(e => e.Note).HasColumnName("note").HasMaxLength(500);
        entity.Property(e => e.UpdatedNotifiedAt).HasColumnName("updated_notified_at");
        entity.Property(e => e.AddedAt).HasColumnName("added_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Upgrade)
            .WithMany(u => u.Lines)
            .HasForeignKey(e => e.UpgradeId)
            .OnDelete(DeleteBehavior.Cascade);

        // An environment Business Central no longer has, or a solution deleted for good,
        // takes its lines with it - the same lifecycle as its action rows.
        entity.HasOne(e => e.Environment)
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.AssigneeUser)
            .WithMany()
            .HasForeignKey(e => e.AssigneeUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.CheckedByUser)
            .WithMany()
            .HasForeignKey(e => e.CheckedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // An environment appears on one upgrade at most once. Also the index behind the
        // upgrade_id cascade, which is why upgrade_id leads.
        entity.HasIndex(e => new { e.UpgradeId, e.EnvironmentId })
            .IsUnique()
            .HasDatabaseName("ux_oe_environment_upgrade_lines_upgrade_env");

        // One open upgrade per environment. The rule is about the *parent* being open, and
        // a Postgres index cannot filter on another table's column, so the line carries a
        // copy of that fact (is_open) which closing and reopening keep in step, and the
        // unique index filters on the copy. EnvironmentUpgradeService checks first so the
        // person gets a sentence naming the other upgrade; the index is what holds when two
        // requests race.
        entity.HasIndex(e => e.EnvironmentId, "ux_oe_environment_upgrade_lines_env_open")
            .IsUnique()
            .HasFilter("is_open");

        // The filtered index above covers only open lines, so the environment_id cascade
        // (deleting an environment row) would scan for the closed ones. This one covers the
        // foreign key's referential action; see CLAUDE.md on zero-scan indexes.
        entity.HasIndex(e => e.EnvironmentId, "ix_oe_environment_upgrade_lines_environment_id");
    }
}
