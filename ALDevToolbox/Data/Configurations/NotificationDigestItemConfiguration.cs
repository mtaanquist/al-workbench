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
        entity.Property(e => e.SolutionName).HasColumnName("solution_name");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // The digest sender's read: one person's items for one delivery, oldest first.
        entity.HasIndex(e => new { e.UserId, e.Delivery, e.CreatedAt });
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
        // ScopeToOrganization<NotificationDigestItem>.
    }
}
