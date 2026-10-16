using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GatewaySeenTokenConfiguration : IEntityTypeConfiguration<GatewaySeenToken>
{
    public void Configure(EntityTypeBuilder<GatewaySeenToken> entity)
    {
        entity.ToTable("gateway_seen_tokens");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.GatewayId).HasColumnName("gateway_id").IsRequired();
        entity.Property(e => e.TokenId).HasColumnName("token_id").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();

        // The replay check itself: inserting a (gateway, jti) pair already here
        // fails, so no read has to cross anything to find out.
        entity.HasIndex(e => new { e.GatewayId, e.TokenId }).IsUnique();
        // The prune on each heartbeat deletes this gateway's expired rows.
        entity.HasIndex(e => new { e.GatewayId, e.ExpiresAt });

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Gateway)
            .WithMany()
            .HasForeignKey(e => e.GatewayId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
