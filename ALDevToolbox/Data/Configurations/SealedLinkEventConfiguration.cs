using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SealedLinkEventConfiguration : IEntityTypeConfiguration<SealedLinkEvent>
{
    public void Configure(EntityTypeBuilder<SealedLinkEvent> entity)
    {
        entity.ToTable("sealed_link_events");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.SealedLinkId).HasColumnName("sealed_link_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        entity.Property(e => e.IpAddress).HasColumnName("ip_address").HasMaxLength(64);

        entity.HasIndex(e => new { e.SealedLinkId, e.OccurredAt });

        entity.HasOne(e => e.SealedLink)
            .WithMany(l => l.Events)
            .HasForeignKey(e => e.SealedLinkId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
