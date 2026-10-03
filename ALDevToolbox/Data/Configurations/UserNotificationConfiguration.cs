using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> entity)
    {
        entity.ToTable("user_notifications");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Category)
            .HasColumnName("category").HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Detail).HasColumnName("detail");
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.SolutionName).HasColumnName("solution_name");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ReadAt).HasColumnName("read_at");

        // The Notifications page: one person's newest first.
        entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        // The header count, read on every page load: only unread rows.
        entity.HasIndex(e => e.UserId)
            .HasFilter("read_at IS NULL")
            .HasDatabaseName("ix_user_notifications_user_id_unread");
        // The 30-day prune.
        entity.HasIndex(e => e.CreatedAt);

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating via
        // ScopeToOrganization<UserNotification>.
    }
}
