using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ReleaseConfiguration : IEntityTypeConfiguration<OeRelease>
{
    public void Configure(EntityTypeBuilder<OeRelease> entity)
    {
        entity.ToTable("oe_releases");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Label).HasColumnName("label").IsRequired();
        entity.Property(e => e.BcVersion).HasColumnName("bc_version");
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.DedupKey).HasColumnName("dedup_key").HasMaxLength(200);
        entity.Property(e => e.IsPrerelease).HasColumnName("is_prerelease").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.Publisher).HasColumnName("publisher").HasMaxLength(200);
        entity.Property(e => e.ProjectName).HasColumnName("project_name").HasMaxLength(200);
        entity.Property(e => e.ParentReleaseId).HasColumnName("parent_release_id");
        entity.Property(e => e.ApplicationVersionId).HasColumnName("application_version_id");
        entity.Property(e => e.Status).HasColumnName("status").IsRequired();
        entity.Property(e => e.StatusMessage).HasColumnName("status_message");
        entity.Property(e => e.ImportedAt).HasColumnName("imported_at").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.Property(e => e.SourceFileCount)
            .HasColumnName("source_file_count")
            .HasDefaultValue(0)
            .IsRequired();
        entity.Property(e => e.SourceContentLength)
            .HasColumnName("source_content_length")
            .HasDefaultValue(0L)
            .IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Parent Release deletion is refused while children still point at it — see
        // .design/object-explorer.md "Storage growth across many Releases" open question.
        entity.HasOne(e => e.ParentRelease)
            .WithMany()
            .HasForeignKey(e => e.ParentReleaseId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.ApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.ApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Dedup is keyed on the explicit dedup_key, not the (now display-only)
        // label: per-org uniqueness on active rows that carry a key. First-party
        // artifact imports set it (bc-onprem:{Maj}.{Min}:{cc}); manual uploads,
        // manual third-party, and project releases leave it null and so never
        // collide; the vendor symbols a pipeline build ingests set
        // symbols:{appId}:{version} (#901).
        // This index is the race backstop behind ArtifactReleaseImporter's
        // pre-check that makes the daily sweep idempotent. See
        // .design/roadmap.md ("Harden first-party dedup, then free the label").
        entity.HasIndex(e => new { e.OrganizationId, e.DedupKey })
            .IsUnique()
            .HasFilter("deleted_at IS NULL AND dedup_key IS NOT NULL")
            .HasDatabaseName("ix_oe_releases_org_dedup_key_active");

        // Chain walk: ancestors and descendants by parent pointer.
        entity.HasIndex(e => e.ParentReleaseId)
            .HasDatabaseName("ix_oe_releases_parent");
    }
}
