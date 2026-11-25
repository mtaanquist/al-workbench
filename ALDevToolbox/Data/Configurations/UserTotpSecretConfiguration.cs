using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserTotpSecretConfiguration : IEntityTypeConfiguration<UserTotpSecret>
{
    public void Configure(EntityTypeBuilder<UserTotpSecret> entity)
    {
        entity.ToTable("user_totp_secrets");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.SecretEncrypted).HasColumnName("secret_encrypted").IsRequired();
        entity.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
        entity.Property(e => e.LastUsedTimeStep).HasColumnName("last_used_time_step");
        entity.HasIndex(e => e.UserId).IsUnique();
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
