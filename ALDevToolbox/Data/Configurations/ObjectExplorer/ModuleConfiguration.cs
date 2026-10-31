using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleConfiguration : IEntityTypeConfiguration<OeModule>
{
    public void Configure(EntityTypeBuilder<OeModule> entity)
    {
        entity.ToTable("oe_modules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ReleaseId).HasColumnName("release_id").IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Publisher).HasColumnName("publisher").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
        entity.Property(e => e.Target).HasColumnName("target");
        entity.Property(e => e.Runtime).HasColumnName("runtime");
        entity.Property(e => e.IsTest).HasColumnName("is_test").IsRequired();
        entity.Property(e => e.IsInternal).HasColumnName("is_internal").IsRequired();
        entity.Property(e => e.IsLanguagePack).HasColumnName("is_language_pack").IsRequired();
        entity.Property(e => e.DependenciesJson).HasColumnName("dependencies_json").HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.DependencyCount).HasColumnName("dependency_count").HasDefaultValue(0).IsRequired();
        entity.Property(e => e.AppFileHash).HasColumnName("app_file_hash");
        entity.Property(e => e.SymbolReferenceContentHash).HasColumnName("symbol_reference_content_hash");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optional link into the shared content store for the raw
        // SymbolReference.json. Restrict (not cascade) so deleting a module
        // never removes content another module's hash still references.
        entity.HasOne(e => e.SymbolReferenceContent)
            .WithMany()
            .HasForeignKey(e => e.SymbolReferenceContentHash)
            .HasPrincipalKey(c => c.ContentHash)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.Release)
            .WithMany(r => r.Modules)
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotency: same (Release, AppId, Version) is silently skipped on re-import.
        // The Version segment of the key lets a child Release legitimately carry a different
        // version of the same AppId without conflicting with the parent's row.
        entity.HasIndex(e => new { e.ReleaseId, e.AppId, e.Version })
            .IsUnique()
            .HasDatabaseName("ix_oe_modules_release_appid_version");

        // Shadowing lookup: the resolver finds candidate modules by AppId, then picks the
        // chain-closest one. Index on (app_id) covers that scan.
        entity.HasIndex(e => e.AppId)
            .HasDatabaseName("ix_oe_modules_appid");

        // Backs the nullable symbol_reference_content_hash FK (OnDelete Restrict):
        // without it the blob reclaim after a release delete or retry
        // (ReleaseManagementService.ReclaimOrphanedBlobsAsync) and any Restrict
        // enforcement on a content delete seq-scan oe_modules. Partial on IS NOT NULL because most
        // modules carry no captured SymbolReference.json. See issue #374, and the
        // parallel ix_oe_module_files_content_hash.
        entity.HasIndex(e => e.SymbolReferenceContentHash)
            .HasDatabaseName("ix_oe_modules_symbol_reference_content_hash")
            .HasFilter("\"symbol_reference_content_hash\" IS NOT NULL");
    }
}
