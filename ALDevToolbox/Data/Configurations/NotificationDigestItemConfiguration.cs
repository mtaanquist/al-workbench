using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class NotificationDigestItemConfiguration : IEntityTypeConfiguration<NotificationDigestItem>
{
    public void Configure(EntityTypeBuilder<NotificationDigestItem> entity)
    {
        entity.ToTable("notification_digest_items");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Category)
            .HasColumnName("category").HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(e => e.Delivery)
            .HasColumnName("delivery").HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Detail).HasColumnName("detail");
        entity.Property(e => e.Url).HasColumnName("url").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id");
        entity.Property(e => e.SolutionName).HasColumnName("solution_name");
        entity.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(64);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // The digest sender's read: one person's items for one delivery, oldest first.
        entity.HasIndex(e => new { e.UserId, e.Delivery, e.CreatedAt });
        // Dropping items whose request was settled before the digest went out.
        entity.HasIndex(e => e.Subject)
            .HasFilter("subject IS NOT NULL")
            .HasDatabaseName("ix_notification_digest_items_subject");
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
        // A solution deleted for good takes its notices with it; covered for the cascade.
        entity.HasIndex(e => e.ProjectId);
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating via
        // ScopeToOrganization<NotificationDigestItem>.
    }
}
