using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Search behaviour for <see cref="RecipeService.SearchAsync"/>: case-insensitive
/// substring match across title, description, and keywords; empty queries
/// return everything; deprecated and soft-deleted rows respect their flags.
/// Recipe Type is deliberately NOT part of the search expression.
/// </summary>
public sealed class RecipeServiceSearchTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Empty_query_returns_every_active_recipe_excluding_soft_deleted()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Alpha").WithFile("a.al", "// a"));
            ctx.Recipes.Add(RecipeBuilder.Default("Beta").WithFile("b.al", "// b"));
            var deleted = RecipeBuilder.Default("Gone").WithFile("g.al", "// g");
            deleted.DeletedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);
            ctx.Recipes.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        var rows = await svc.SearchAsync(query: null);
        rows.Select(r => r.Title).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
    }

    [Fact]
    public async Task Query_matches_title_case_insensitively()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Doc Attachment Factbox").WithFile("a.al", "// a"));
            ctx.Recipes.Add(RecipeBuilder.Default("Item Lookup Helper").WithFile("b.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("FACTBOX");
        rows.Should().ContainSingle(r => r.Title == "Doc Attachment Factbox");
    }

    /// <summary>
    /// #1267 (L41): "_" and "%" are characters to search for, not wildcards.
    /// An unescaped "_" matched every recipe.
    /// </summary>
    [Theory]
    [InlineData("_", "Stock_Level Probe")]
    [InlineData("%", "100% Coverage Check")]
    public async Task Like_wildcards_in_the_query_match_only_themselves(string query, string expected)
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Stock_Level Probe").WithFile("a.al", "// a"));
            ctx.Recipes.Add(RecipeBuilder.Default("100% Coverage Check").WithFile("b.al", "// b"));
            ctx.Recipes.Add(RecipeBuilder.Default("Item Lookup Helper").WithFile("c.al", "// c"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync(query);
        rows.Select(r => r.Title).Should().Equal(expected);
    }

    [Fact]
    public async Task Query_matches_description_substring()
    {
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Hidden Title").WithFile("a.al", "// a");
            s.Description = "Use this to wire up subscriber events.";
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("subscriber");
        rows.Should().ContainSingle(r => r.Title == "Hidden Title");
    }

    [Fact]
    public async Task Query_matches_keywords_substring()
    {
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Tagged").WithFile("a.al", "// a");
            s.Keywords = "permissions tableextension";
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("permission");
        rows.Should().ContainSingle(r => r.Title == "Tagged");
    }

    [Fact]
    public async Task Deprecated_rows_are_hidden_by_default_and_shown_when_requested()
    {
        await using (var ctx = _db.NewContext())
        {
            var depr = RecipeBuilder.Default("Old Way").WithFile("a.al", "// a");
            depr.Deprecated = true;
            ctx.Recipes.Add(depr);
            ctx.Recipes.Add(RecipeBuilder.Default("New Way").WithFile("b.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var withoutDeprecated = await NewService(ctx2).SearchAsync(query: null, includeDeprecated: false);
        withoutDeprecated.Select(r => r.Title).Should().BeEquivalentTo(new[] { "New Way" });

        await using var ctx3 = _db.NewContext();
        var withDeprecated = await NewService(ctx3).SearchAsync(query: null, includeDeprecated: true);
        withDeprecated.Select(r => r.Title).Should().BeEquivalentTo(new[] { "Old Way", "New Way" });
    }

    [Fact]
    public async Task Search_ignores_recipe_type_and_returns_all_types()
    {
        // Type is a post-filter on the UI side, never feeding the query.
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Snip", type: RecipeType.Snippet).WithFile("a.al", "// a"));
            ctx.Recipes.Add(RecipeBuilder.Default("Pat", type: RecipeType.Pattern).WithFile("b.al", "// b"));
            ctx.Recipes.Add(RecipeBuilder.Default("Mod", type: RecipeType.Module).WithFile("c.al", "// c"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync(query: null);
        rows.Select(r => r.Title).Should().BeEquivalentTo(new[] { "Snip", "Pat", "Mod" });
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx));
}
