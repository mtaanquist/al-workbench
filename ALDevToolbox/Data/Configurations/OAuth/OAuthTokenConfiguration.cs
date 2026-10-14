using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace ALDevToolbox.Data.Configurations.OAuth;

/// <summary>
/// Snake_case for OpenIddict's <c>oauth_tokens</c> table. Holds both access
/// and refresh tokens; OpenIddict's manager differentiates by the
/// <c>type</c> column. Reference + payload columns store the
/// Data-Protection-wrapped opaque tokens emitted by the server.
/// </summary>
internal sealed class OAuthTokenConfiguration : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreToken>
{
    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreToken> entity)
    {
        entity.ToTable("oauth_tokens");
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.ConcurrencyToken).HasColumnName("concurrency_token");
        entity.Property(e => e.CreationDate).HasColumnName("creation_date");
        entity.Property(e => e.ExpirationDate).HasColumnName("expiration_date");
        entity.Property(e => e.Payload).HasColumnName("payload");
        entity.Property(e => e.Properties).HasColumnName("properties");
        entity.Property(e => e.RedemptionDate).HasColumnName("redemption_date");
        entity.Property(e => e.ReferenceId).HasColumnName("reference_id");
        entity.Property(e => e.Status).HasColumnName("status");
        entity.Property(e => e.Subject).HasColumnName("subject");
        entity.Property(e => e.Type).HasColumnName("type");

        // Rename the shadow FKs that UseOpenIddict() installs on the
        // Application + Authorization navigations, in place — declaring new
        // HasForeignKey clauses would create duplicate shadow columns.
        entity.Property<string>("ApplicationId").HasColumnName("application_id");
        entity.Property<string>("AuthorizationId").HasColumnName("authorization_id");

        // Disabling a user revokes every token for that subject (#1259).
        // OpenIddict's own index leads with application_id, so a lookup by
        // subject alone would scan the table.
        entity.HasIndex(e => e.Subject)
            .HasDatabaseName("ix_oauth_tokens_subject");
    }
}
