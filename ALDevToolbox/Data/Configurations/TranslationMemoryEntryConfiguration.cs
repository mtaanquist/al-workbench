using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class TranslationMemoryEntryConfiguration : IEntityTypeConfiguration<TranslationMemoryEntry>
{
    public void Configure(EntityTypeBuilder<TranslationMemoryEntry> entity)
    {
        entity.ToTable("translation_memory");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.SourceLanguage).HasColumnName("source_language").IsRequired();
        entity.Property(e => e.TargetLanguage).HasColumnName("target_language").IsRequired();
        entity.Property(e => e.SourceText).HasColumnName("source_text").IsRequired();
        entity.Property(e => e.TargetText).HasColumnName("target_text").IsRequired();
        entity.Property(e => e.SourceHash).HasColumnName("source_hash").IsRequired();
        entity.Property(e => e.TargetHash).HasColumnName("target_hash").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.Origin).HasColumnName("origin");
        entity.Property(e => e.SourceRepository).HasColumnName("source_repository").HasMaxLength(300);
        entity.Property(e => e.SourcePath).HasColumnName("source_path").HasMaxLength(1000);
        entity.Property(e => e.HitCount).HasColumnName("hit_count").IsRequired();
        entity.Property(e => e.Score).HasColumnName("score").IsRequired().HasDefaultValue(0);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Candidate lookup for exact matches + the per-(org,language) scan that
        // precedes the trigram fuzzy search.
        entity.HasIndex(e => new { e.OrganizationId, e.SourceLanguage, e.TargetLanguage, e.SourceHash })
            .HasDatabaseName("ix_translation_memory_lookup");

        // The natural key. Hashes keep the index entries bounded — the raw
        // source/target text can exceed a btree key. Re-seeing a pair updates
        // the existing row (hit_count / last_seen) instead of duplicating.
        entity.HasIndex(e => new { e.OrganizationId, e.SourceLanguage, e.TargetLanguage, e.SourceHash, e.TargetHash })
            .IsUnique()
            .HasDatabaseName("ux_translation_memory_pair");

        // GIN trigram index powering the fuzzy `%` / similarity() suggestion
        // query. Requires the pg_trgm extension (declared on the model in
        // AppDbContext.OnModelCreating).
        entity.HasIndex(e => e.SourceText)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_translation_memory_source_trgm");

        // The management page searches source *and* target text with ILIKE, and a
        // pg_trgm GIN index serves ILIKE — without this one the target half of
        // that OR forces a sequential scan over the whole table. #686
        entity.HasIndex(e => e.TargetText)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_translation_memory_target_trgm");

        // The whole browse ordering (score, hit_count, last_seen_at, then id)
        // inside an organisation, so a page is read straight off the index. With
        // only score and hit_count in it, Postgres still sorted every row of the
        // largest tie group (most of the memory: score 0, seen once) to find
        // the first 50.
        entity.HasIndex(e => new { e.OrganizationId, e.Score, e.HitCount, e.LastSeenAt, e.Id })
            .IsDescending(false, true, true, true, true)
            .HasDatabaseName("ix_translation_memory_rank");
    }
}
