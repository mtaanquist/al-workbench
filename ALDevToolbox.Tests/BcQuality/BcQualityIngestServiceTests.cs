using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.BcQuality;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.BcQuality;

/// <summary>
/// The BCQuality walker and upsert contract (see <c>.design/bcquality.md</c>).
/// Every test drives <c>IngestFromDirectoryAsync</c> against a temp checkout —
/// the git half is exercised by hand against the real repository, never from
/// the test suite.
/// </summary>
public sealed class BcQualityIngestServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BcQualityRepoFixture _repo = new();

    private const string UiArticle = "microsoft/knowledge/ui/no-nested-grids.md";
    private const string PerfArticle = "microsoft/knowledge/performance/apply-filters-before-iterating.md";

    public void Dispose()
    {
        _repo.Dispose();
        _db.Dispose();
    }

    private async Task<BcQualityIngestResult> IngestAsync(string sha = "abc123")
    {
        await using var ctx = _db.NewContext();
        return await BcQualityTestServices.NewIngestService(ctx)
            .IngestFromDirectoryAsync(_repo.Root, sha, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Ingest_stores_articles_with_their_frontmatter_and_samples()
    {
        _repo.WriteArticle(UiArticle);
        _repo.WriteSample(UiArticle, "bad", "al", "// the anti-pattern");
        _repo.WriteSample(UiArticle, "good", "al", "// the fix");
        _repo.WriteArticle(PerfArticle,
            title: "Apply filters before iterating",
            description: "Filter the record before looping over it.",
            domain: "performance",
            keywords: "[filtering, setloadfields]");

        var result = await IngestAsync();

        result.Added.Should().Be(2);
        result.Total.Should().Be(2);
        result.Pruned.Should().Be(0);

        await using var ctx = _db.NewContext();
        var article = await ctx.BcQualityArticles.AsNoTracking()
            .Include(a => a.Samples)
            .SingleAsync(a => a.ArticleKey == UiArticle);

        article.Layer.Should().Be("microsoft");
        article.Domain.Should().Be("ui");
        article.Slug.Should().Be("no-nested-grids");
        article.Title.Should().Be("Nested grids are not supported");
        article.Summary.Should().Be("A grid nested inside another grid is not a supported pattern.");
        article.Keywords.Should().BeEquivalentTo("grid", "nested-grid", "accessibility");
        article.Technologies.Should().BeEquivalentTo("al");
        article.Countries.Should().BeEquivalentTo("w1");
        article.ApplicationAreas.Should().BeEquivalentTo("all");
        article.Content.Should().Contain("## Description").And.NotContain("bc-version:");
        article.Samples.Should().HaveCount(2);
        article.Samples.Select(s => s.Kind).Should().BeEquivalentTo("good", "bad");
        article.Samples.Single(s => s.Kind == "bad").Content.Should().Be("// the anti-pattern");
        article.Samples.Single(s => s.Kind == "bad").Language.Should().Be("al");
    }

    /// <summary>
    /// #1268 (L47): a link in the upstream checkout is never read through. A
    /// sample linked to a secret outside the clone would otherwise land in
    /// rows every organisation can read.
    /// </summary>
    [Fact]
    public async Task Ingest_does_not_follow_links_out_of_the_checkout()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aldt-bcq-secret-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "SECRET=hunter2");
        var outsideArticle = outside + ".md";
        File.WriteAllText(outsideArticle, File.ReadAllText(_repo.WriteArticle(UiArticle)));
        try
        {
            var folder = Path.Combine(_repo.Root, "microsoft", "knowledge", "ui");
            File.CreateSymbolicLink(Path.Combine(folder, "no-nested-grids.bad.al"), outside);
            File.CreateSymbolicLink(Path.Combine(folder, "linked-article.md"), outsideArticle);

            var result = await IngestAsync();

            result.Total.Should().Be(1, "the linked article is not one of the repository's own");
            await using var ctx = _db.NewContext();
            (await ctx.BcQualityArticleSamples.AsNoTracking().CountAsync()).Should().Be(0);
            (await ctx.BcQualityArticles.AsNoTracking().Select(a => a.ArticleKey).ToListAsync())
                .Should().Equal(UiArticle);
        }
        finally
        {
            File.Delete(outside);
            File.Delete(outsideArticle);
        }
    }

    [Fact]
    public async Task Ingest_records_the_commit_it_read_for_provenance()
    {
        _repo.WriteArticle(UiArticle);

        await IngestAsync("deadbeefcafe");

        await using var ctx = _db.NewContext();
        var state = await BcQualityTestServices.NewIngestService(ctx).GetStateAsync();
        state.Should().NotBeNull();
        state!.CommitSha.Should().Be("deadbeefcafe");
        state.ArticleCount.Should().Be(1);
        state.LastSuccessAt.Should().NotBeNull();
        state.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_over_an_unchanged_checkout_writes_nothing()
    {
        _repo.WriteArticle(UiArticle);
        _repo.WriteSample(UiArticle, "good", "al", "// the fix");
        await IngestAsync();

        var second = await IngestAsync();

        second.Added.Should().Be(0);
        second.Updated.Should().Be(0);
        second.Unchanged.Should().Be(1);
        second.Pruned.Should().Be(0);
    }

    [Fact]
    public async Task Ingest_updates_a_changed_article_and_keeps_its_first_seen_date()
    {
        _repo.WriteArticle(UiArticle);
        await IngestAsync();
        DateTime firstSeen;
        await using (var ctx = _db.NewContext())
        {
            firstSeen = (await ctx.BcQualityArticles.AsNoTracking().SingleAsync()).FirstSeenAt;
        }

        _repo.WriteArticle(UiArticle, description: "Rewritten guidance about nested grids.");
        var result = await IngestAsync();

        result.Updated.Should().Be(1);
        result.Added.Should().Be(0);

        await using var check = _db.NewContext();
        var article = await check.BcQualityArticles.AsNoTracking().SingleAsync();
        article.Summary.Should().Be("Rewritten guidance about nested grids.");
        article.FirstSeenAt.Should().Be(firstSeen);
    }

    [Fact]
    public async Task Ingest_notices_a_sample_that_changed_without_the_article()
    {
        _repo.WriteArticle(UiArticle);
        _repo.WriteSample(UiArticle, "bad", "al", "// version one");
        await IngestAsync();

        _repo.WriteSample(UiArticle, "bad", "al", "// version two");
        var result = await IngestAsync();

        result.Updated.Should().Be(1);
        await using var ctx = _db.NewContext();
        var samples = await ctx.BcQualityArticleSamples.AsNoTracking().ToListAsync();
        samples.Should().ContainSingle().Which.Content.Should().Be("// version two");
    }

    [Fact]
    public async Task Ingest_prunes_an_article_that_disappeared_upstream()
    {
        _repo.WriteArticle(UiArticle);
        _repo.WriteSample(UiArticle, "bad", "al", "// gone soon");
        _repo.WriteArticle(PerfArticle, domain: "performance");
        await IngestAsync();

        _repo.Delete(UiArticle);
        var result = await IngestAsync();

        result.Pruned.Should().Be(1);
        await using var ctx = _db.NewContext();
        (await ctx.BcQualityArticles.AsNoTracking().Select(a => a.ArticleKey).ToListAsync())
            .Should().Equal(PerfArticle);
        // The cascade takes the orphaned samples with it.
        (await ctx.BcQualityArticleSamples.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Ingest_reads_every_layer_but_ignores_the_skills_trees()
    {
        _repo.WriteArticle("microsoft/knowledge/ui/no-nested-grids.md");
        _repo.WriteArticle("community/knowledge/ui/factbox-design.md", title: "Filter ListPart FactBoxes");
        _repo.WriteArticle("custom/knowledge/style/house-naming.md", title: "House naming", domain: "style");
        // Action skills and repo docs are not guidance and are not ingested.
        _repo.WriteArticle("microsoft/skills/review/al-ui-review.md", title: "AL UI review");
        _repo.WriteArticle("skills/read.md", title: "Schema and use");

        var result = await IngestAsync();

        result.Added.Should().Be(3);
        await using var ctx = _db.NewContext();
        (await ctx.BcQualityArticles.AsNoTracking().Select(a => a.Layer).ToListAsync())
            .Should().BeEquivalentTo("microsoft", "community", "custom");
    }

    [Theory]
    // The four accepted forms from BCQuality's schema contract.
    [InlineData("[all]", true, new int[0], null)]
    [InlineData("[26, 27, 28]", false, new[] { 26, 27, 28 }, null)]
    [InlineData("[24..26]", false, new[] { 24, 25, 26 }, null)]
    [InlineData("[23..]", false, new int[0], 23)]
    public async Task Ingest_expands_every_bc_version_form(
        string raw, bool expectAll, int[] expectVersions, int? expectFrom)
    {
        _repo.WriteArticle(UiArticle, bcVersion: raw);

        await IngestAsync();

        await using var ctx = _db.NewContext();
        var article = await ctx.BcQualityArticles.AsNoTracking().SingleAsync();
        article.BcVersionRaw.Should().Be(raw);
        article.BcVersionAll.Should().Be(expectAll);
        article.BcVersions.Should().Equal(expectVersions);
        article.BcVersionFrom.Should().Be(expectFrom);
    }

    [Fact]
    public async Task Ingest_skips_a_file_that_violates_the_schema_and_says_why()
    {
        _repo.WriteArticle(UiArticle);
        // No frontmatter at all.
        _repo.WriteRaw("microsoft/knowledge/ui/loose-note.md", "# Just a note\n\nNothing structured here.\n");
        // Frontmatter, but missing two required fields.
        _repo.WriteRaw("microsoft/knowledge/ui/half-tagged.md",
            "---\nbc-version: [all]\ndomain: ui\nkeywords: [a, b]\ntechnologies: [al]\n---\n\n# Half tagged\n\n## Description\n\nText.\n");
        // Frontmatter, but no Description section.
        _repo.WriteRaw("microsoft/knowledge/ui/no-description.md",
            "---\nbc-version: [all]\ndomain: ui\nkeywords: [a]\ntechnologies: [al]\ncountries: [w1]\napplication-area: [all]\n---\n\n# No description\n\n## Best Practice\n\nText.\n");
        // A folder README is documentation about the folder, not guidance.
        _repo.WriteRaw("microsoft/knowledge/ui/README.md", "# UI knowledge\n");

        var result = await IngestAsync();

        result.Added.Should().Be(1);
        result.Skipped.Should().HaveCount(4);
        result.Skipped.Single(s => s.Path.EndsWith("loose-note.md", StringComparison.Ordinal))
            .Reason.Should().Contain("frontmatter");
        result.Skipped.Single(s => s.Path.EndsWith("half-tagged.md", StringComparison.Ordinal))
            .Reason.Should().Contain("countries").And.Contain("application-area");
        result.Skipped.Single(s => s.Path.EndsWith("no-description.md", StringComparison.Ordinal))
            .Reason.Should().Contain("Description");
        result.Skipped.Single(s => s.Path.EndsWith("README.md", StringComparison.Ordinal))
            .Reason.Should().Contain("README");
    }

    [Fact]
    public async Task Ingest_skips_an_unparseable_bc_version_rather_than_guessing()
    {
        _repo.WriteArticle(UiArticle, bcVersion: "[twenty-six]");

        var act = () => IngestAsync();

        // The only article was invalid, so the whole checkout is refused —
        // which is the guard that keeps a bad tree from pruning a good mirror.
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("root");
    }

    [Fact]
    public async Task Ingest_refuses_a_directory_that_does_not_exist()
    {
        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewIngestService(ctx);

        var act = () => service.IngestFromDirectoryAsync(
            Path.Combine(_repo.Root, "nope"), "sha", null);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("root");
    }

    [Fact]
    public async Task Ingest_refuses_a_checkout_with_no_articles_so_a_bad_clone_cannot_empty_the_mirror()
    {
        _repo.WriteArticle(UiArticle);
        await IngestAsync();

        using var empty = new BcQualityRepoFixture();
        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewIngestService(ctx);

        var act = () => service.IngestFromDirectoryAsync(empty.Root, "sha", null);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["root"].Should().Contain("No BCQuality knowledge articles");

        await using var check = _db.NewContext();
        (await check.BcQualityArticles.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Ingest_ignores_a_sibling_that_only_shares_a_name_prefix()
    {
        _repo.WriteArticle(UiArticle);
        _repo.WriteSample(UiArticle, "good", "al", "// mine");
        // Same folder, name starts with the slug but is a different article.
        _repo.WriteArticle("microsoft/knowledge/ui/no-nested-grids-in-parts.md", title: "Different article");

        await IngestAsync();

        await using var ctx = _db.NewContext();
        var article = await ctx.BcQualityArticles.AsNoTracking()
            .Include(a => a.Samples)
            .SingleAsync(a => a.ArticleKey == UiArticle);
        article.Samples.Should().ContainSingle().Which.FileName.Should().Be("no-nested-grids.good.al");
    }
}
