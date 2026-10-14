using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectRepositoryConfiguration : IEntityTypeConfiguration<OeProjectRepository>
{
    public void Configure(EntityTypeBuilder<OeProjectRepository> entity)
    {
        entity.ToTable("oe_project_repositories");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        // Stored as the short string discriminator (azure_devops / github), not the
        // enum's ordinal, so the column reads meaningfully and is stable if the enum reorders.
        entity.Property(e => e.Provider)
            .HasColumnName("provider")
            .HasMaxLength(40)
            .IsRequired()
            .HasConversion(
                p => p.ToDiscriminator(),
                s => RepositoryProviders.FromDiscriminatorStrict(s));
        entity.Property(e => e.Url).HasColumnName("url").HasMaxLength(2000).IsRequired();
        entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.AddedByUserId).HasColumnName("added_by_user_id");
        entity.Property(e => e.AccessConfirmedAt).HasColumnName("access_confirmed_at");
        entity.Property(e => e.AccessConfirmedByUserId).HasColumnName("access_confirmed_by_user_id");
        entity.Ignore(e => e.IsAccessConfirmed);

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Who added the row and who confirmed they can read it. SET NULL on delete:
        // the confirmation stands as a record of when, and the row stays buildable.
        // Each FK has its own index so deleting a user does not scan the table.
        entity.HasOne(e => e.AddedByUser)
            .WithMany()
            .HasForeignKey(e => e.AddedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(e => e.AddedByUserId)
            .HasDatabaseName("ix_oe_project_repositories_added_by_user");
        entity.HasOne(e => e.AccessConfirmedByUser)
            .WithMany()
            .HasForeignKey(e => e.AccessConfirmedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(e => e.AccessConfirmedByUserId)
            .HasDatabaseName("ix_oe_project_repositories_access_confirmed_by_user");

        // The Project -> Repositories relationship (FK project_id, cascade) is
        // configured on the Project side; here we just index the FK for the
        // per-project repo listing.
        entity.HasIndex(e => e.ProjectId)
            .HasDatabaseName("ix_oe_project_repositories_project");
    }
}
