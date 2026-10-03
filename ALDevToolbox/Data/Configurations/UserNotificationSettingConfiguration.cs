using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserNotificationSettingConfiguration : IEntityTypeConfiguration<UserNotificationSetting>
{
    public void Configure(EntityTypeBuilder<UserNotificationSetting> entity)
    {
        entity.ToTable("user_notification_settings");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Category)
            .HasColumnName("category").HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(e => e.Delivery)
            .HasColumnName("delivery").HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(e => e.InApp).HasColumnName("in_app").HasDefaultValue(true).IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // One choice per person per category: the upsert key, and the lookup
        // the sender makes for each event's recipients.
        entity.HasIndex(e => new { e.UserId, e.Category }).IsUnique();

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating via
        // ScopeToOrganization<UserNotificationSetting>.
    }
}
