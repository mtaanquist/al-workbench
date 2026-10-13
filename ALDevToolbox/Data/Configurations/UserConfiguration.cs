using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("users");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
        entity.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired();
        entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        entity.Property(e => e.IsSiteAdmin).HasColumnName("is_site_admin").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
        entity.Property(e => e.TotpEnabled).HasColumnName("totp_enabled").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.EmailMfaEnabled).HasColumnName("email_mfa_enabled").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.PendingEmail).HasColumnName("pending_email");
        entity.Property(e => e.PendingEmailAt).HasColumnName("pending_email_at");
        entity.Property(e => e.CredentialsChangedAt).HasColumnName("credentials_changed_at");
        // Unique deployment-wide, not per organisation: sign-in routes an email to
        // exactly one account, so two orgs holding the same address is a broken
        // state rather than a supported one (issue #1207).
        entity.HasIndex(e => e.Email).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
