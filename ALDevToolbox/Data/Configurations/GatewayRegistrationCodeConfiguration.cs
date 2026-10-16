using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GatewayRegistrationCodeConfiguration : IEntityTypeConfiguration<GatewayRegistrationCode>
{
    public void Configure(EntityTypeBuilder<GatewayRegistrationCode> entity)
    {
        entity.ToTable("gateway_registration_codes");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CodeHash).HasColumnName("code_hash").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        // The concurrency token is what makes a code single-use under a race: the
        // UPDATE that stamps it carries "AND used_at IS NULL".
        entity.Property(e => e.UsedAt).HasColumnName("used_at").IsConcurrencyToken();
        entity.Property(e => e.GatewayId).HasColumnName("gateway_id");

        entity.HasIndex(e => e.CodeHash).IsUnique();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(e => e.Gateway)
            .WithMany()
            .HasForeignKey(e => e.GatewayId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
