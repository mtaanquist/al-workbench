using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GatewayConfiguration : IEntityTypeConfiguration<Gateway>
{
    public void Configure(EntityTypeBuilder<Gateway> entity)
    {
        entity.ToTable("gateways");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.PublicKey).HasColumnName("public_key").IsRequired();
        // The concurrency token is what keeps an approval honest: the UPDATE that
        // approves carries "AND key_fingerprint = <the one the Admin compared>", so a
        // rotation landing between the read and the save fails the approval instead
        // of approving a key nobody looked at. Model-only; no schema change.
        entity.Property(e => e.KeyFingerprint).HasColumnName("key_fingerprint").IsRequired().IsConcurrencyToken();
        entity.Property(e => e.State).HasColumnName("state").HasConversion<string>().IsRequired();
        entity.Property(e => e.RegisteredAt).HasColumnName("registered_at").IsRequired();
        entity.Property(e => e.KeyRegisteredAt).HasColumnName("key_registered_at").IsRequired();
        entity.Property(e => e.ApprovedByUserId).HasColumnName("approved_by_user_id");
        entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
        entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        entity.Property(e => e.ReportedVersion).HasColumnName("reported_version");
        entity.Property(e => e.MachineName).HasColumnName("machine_name").IsRequired();
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");

        entity.HasIndex(e => new { e.OrganizationId, e.RevokedAt });

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.ApprovedByUser)
            .WithMany()
            .HasForeignKey(e => e.ApprovedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Query filter installed in AppDbContext.OnModelCreating.
    }
}
