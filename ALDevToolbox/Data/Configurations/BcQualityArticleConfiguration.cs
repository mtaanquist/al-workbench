using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

/// <summary>
/// Schema for the mirrored BCQuality knowledge base. See
/// <c>.design/bcquality.md</c>.
///
/// <para>
/// No <c>organization_id</c> and no query filter, by design: the content is
/// public and identical for every tenant.
/// </para>
/// </summary>
internal sealed class BcQualityArticleConfiguration : IEntityTypeConfiguration<BcQualityArticle>
{
    /// <summary>
    /// The stored generated column behind <c>search_bcquality</c>. Weighting
    /// puts a title hit above a keyword hit above a body hit, which is what
    /// makes ranking useful on a corpus where every article talks about AL.
    /// Postgres maintains it on write, so an ingest never has to compute it —
    /// and it can never drift from the row it indexes.
    ///
    /// <para>
    /// Every function here is IMMUTABLE, which is the requirement for a
    /// generated column: <c>to_tsvector</c> with an explicit configuration,
    /// <c>setweight</c>, <c>coalesce</c>. That is also why the B group reads
    /// <c>keywords_text</c> and not the <c>keywords</c> array —
    /// <c>array_to_string</c> is only STABLE and Postgres rejects it here.
    /// </para>
    /// </summary>
    internal const string SearchVectorSql = """
        setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
        setweight(to_tsvector('english', coalesce(keywords_text, '') || ' ' || coalesce(domain, '')), 'B') ||
        setweight(to_tsvector('english', coalesce(summary, '')), 'C') ||
        setweight(to_tsvector('english', coalesce(content, '')), 'D')
        """;

    public void Configure(EntityTypeBuilder<BcQualityArticle> entity)
    {
        entity.ToTable("bcquality_articles");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.ArticleKey).HasColumnName("article_key").IsRequired();
        entity.Property(e => e.Layer).HasColumnName("layer").IsRequired();
        entity.Property(e => e.Domain).HasColumnName("domain").IsRequired();
        entity.Property(e => e.Slug).HasColumnName("slug").IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Summary).HasColumnName("summary").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.PrimitiveCollection(e => e.Keywords).HasColumnName("keywords").IsRequired();
        entity.Property(e => e.KeywordsText).HasColumnName("keywords_text").IsRequired();
        entity.PrimitiveCollection(e => e.Technologies).HasColumnName("technologies").IsRequired();
        entity.PrimitiveCollection(e => e.Countries).HasColumnName("countries").IsRequired();
        entity.PrimitiveCollection(e => e.ApplicationAreas).HasColumnName("application_areas").IsRequired();
        entity.Property(e => e.BcVersionRaw).HasColumnName("bc_version_raw").IsRequired();
        entity.Property(e => e.BcVersionAll).HasColumnName("bc_version_all").IsRequired();
        entity.PrimitiveCollection(e => e.BcVersions).HasColumnName("bc_versions").IsRequired();
        entity.Property(e => e.BcVersionFrom).HasColumnName("bc_version_from");
        entity.Property(e => e.ContentHash).HasColumnName("content_hash").IsRequired();
        entity.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.Property(e => e.SearchVector)
            .HasColumnName("search_vector")
            .HasColumnType("tsvector")
            .HasComputedColumnSql(SearchVectorSql, stored: true);

        // The upsert key: one row per repo-relative path.
        entity.HasIndex(e => e.ArticleKey)
            .IsUnique()
            .HasDatabaseName("ux_bcquality_articles_key");

        // Domain is both a filter on search_bcquality and how BCQuality's own
        // skills scope their candidate set.
        entity.HasIndex(e => e.Domain).HasDatabaseName("ix_bcquality_articles_domain");

        entity.HasIndex(e => e.SearchVector)
            .HasMethod("gin")
            .HasDatabaseName("ix_bcquality_articles_search");

        entity.HasMany(e => e.Samples)
            .WithOne(s => s.Article!)
            .HasForeignKey(s => s.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BcQualityArticleSampleConfiguration : IEntityTypeConfiguration<BcQualityArticleSample>
{
    public void Configure(EntityTypeBuilder<BcQualityArticleSample> entity)
    {
        entity.ToTable("bcquality_article_samples");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.ArticleId).HasColumnName("article_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Language).HasColumnName("language").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();

        entity.HasIndex(e => new { e.ArticleId, e.FileName })
            .IsUnique()
            .HasDatabaseName("ux_bcquality_article_samples_file");
    }
}

internal sealed class BcQualityIngestStateConfiguration : IEntityTypeConfiguration<BcQualityIngestState>
{
    public void Configure(EntityTypeBuilder<BcQualityIngestState> entity)
    {
        entity.ToTable("bcquality_ingest_state");
        entity.HasKey(e => e.Id);
        // Never generated: the row is the fixed singleton id 1.
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(e => e.CommitSha).HasColumnName("commit_sha").IsRequired();
        entity.Property(e => e.CommitDate).HasColumnName("commit_date");
        entity.Property(e => e.LastSuccessAt).HasColumnName("last_success_at");
        entity.Property(e => e.LastAttemptAt).HasColumnName("last_attempt_at");
        entity.Property(e => e.ArticleCount).HasColumnName("article_count").IsRequired();
        entity.Property(e => e.LastError).HasColumnName("last_error").IsRequired();
    }
}
