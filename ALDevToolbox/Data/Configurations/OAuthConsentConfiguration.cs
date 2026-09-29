using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OAuthConsentConfiguration : IEntityTypeConfiguration<OAuthConsent>
{
    public void Configure(EntityTypeBuilder<OAuthConsent> entity)
    {
        entity.ToTable("oauth_consents");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
        entity.Property(e => e.ScopesGranted).HasColumnName("scopes_granted").IsRequired();
        entity.Property(e => e.GrantedAt).HasColumnName("granted_at").IsRequired();
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.StrongAuthAt).HasColumnName("strong_auth_at");

        // One active consent per (user, client, org). When the user revokes
        // and later re-consents we update RevokedAt back to null on the same
        // row — keeps the audit trail intact.
        entity.HasIndex(e => new { e.UserId, e.ClientId, e.OrganizationId }).IsUnique();

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
