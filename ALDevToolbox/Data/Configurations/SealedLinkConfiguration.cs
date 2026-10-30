using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SealedLinkConfiguration : IEntityTypeConfiguration<SealedLink>
{
    public void Configure(EntityTypeBuilder<SealedLink> entity)
    {
        entity.ToTable("sealed_links");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        entity.Property(e => e.Token).HasColumnName("token").HasMaxLength(32).IsRequired();
        entity.Property(e => e.Label).HasColumnName("label").HasMaxLength(100);
        entity.Property(e => e.Ciphertext).HasColumnName("ciphertext");
        entity.Property(e => e.Iv).HasColumnName("iv").IsRequired();
        entity.Property(e => e.LinkCheckHash).HasColumnName("link_check_hash").IsRequired();
        entity.Property(e => e.PassphraseSalt).HasColumnName("passphrase_salt");
        entity.Property(e => e.PassphraseCheckHash).HasColumnName("passphrase_check_hash");
        entity.Property(e => e.FailedPassphraseAttempts).HasColumnName("failed_passphrase_attempts").IsRequired();
        entity.Property(e => e.MaxViews).HasColumnName("max_views").IsRequired();
        entity.Property(e => e.ViewCount).HasColumnName("view_count").IsRequired();
        entity.Property(e => e.NotifiedViewCount).HasColumnName("notified_view_count").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
        entity.Property(e => e.ClosedReason).HasColumnName("closed_reason").HasConversion<string>().HasMaxLength(16);
        entity.Property(e => e.LockoutNotified).HasColumnName("lockout_notified").IsRequired();

        // The recipient's lookup, the only way a link is found without a signed-in org.
        entity.HasIndex(e => e.Token).IsUnique();
        // The sender's list, newest first.
        entity.HasIndex(e => new { e.OrganizationId, e.CreatedByUserId, e.CreatedAt });
        // The sweep: open links past their expiry, and closed ones past retention.
        entity.HasIndex(e => new { e.OrganizationId, e.ExpiresAt });

        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating.
    }
}
