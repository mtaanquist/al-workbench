using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ALDevToolbox.Services.Cookbook;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// Behaviour for the MCP tool wrappers under <see cref="Services.Mcp.Tools"/>.
/// The tools are thin shims over existing services — the tests guard the
/// contract at the MCP boundary: input mapping, error surfacing as
/// <see cref="McpException"/> instead of raw <see cref="PlanValidationException"/>,
/// the inline-base64 ZIP, and the cookbook org filter.
/// </summary>
public sealed class McpToolTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly IOptions<McpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new McpOptions());

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedSubmitterAsync(int userId = 800)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"mcp{userId}@example.com",
            PasswordHash = "x",
            DisplayName = "MCP Submitter",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = userId;
        return userId;
    }

    // ---- CookbookTools -----------------------------------------------------

    [Fact]
    public async Task SearchRecipes_returns_summaries_for_active_rows_in_caller_org()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Posting Validation Override", type: RecipeType.Pattern)
                .WithFile("Codeunit.al", "// override")
                .WithFile("Test.al", "// test"));
            ctx.Recipes.Add(RecipeBuilder.Default("Item Lookup Helper"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var rows = (await tools.SearchAsync(query: "posting")).Rows;

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Posting Validation Override");
        rows[0].Type.Should().Be("Pattern");
        rows[0].FileCount.Should().Be(2);
    }

    [Fact]
    public async Task GetRecipe_returns_file_payload_with_relative_path_joined()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Sample")
                .WithFile("a.al", "// hello")
                .WithFile("b.al", "// nested", relativePath: "sub");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var detail = await tools.GetAsync(recipeId);
        detail.Title.Should().Be("Sample");
        detail.Type.Should().Be("Snippet");
        detail.Files.Should().HaveCount(2);
        detail.Files.Select(f => f.Path).Should().Contain(new[] { "a.al", "sub/b.al" });
    }

    [Fact]
    public async Task SearchRecipes_stops_at_the_limit_in_title_order_and_says_so()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("CRONUS C"));
            ctx.Recipes.Add(RecipeBuilder.Default("CRONUS A"));
            ctx.Recipes.Add(RecipeBuilder.Default("CRONUS B"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var cut = await tools.SearchAsync(query: "cronus", limit: 2);
        cut.Rows.Select(r => r.Title).Should().Equal("CRONUS A", "CRONUS B");
        cut.Truncated.Should().BeTrue();
        cut.Note.Should().Contain("first 2 recipes");

        var whole = await tools.SearchAsync(query: "cronus", limit: 3);
        whole.Rows.Should().HaveCount(3);
        whole.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task SearchRecipes_clamps_the_limit_and_cuts_long_descriptions_but_not_keywords()
    {
        var keywords = string.Join(", ", Enumerable.Range(1, 60).Select(i => $"keyword{i}"));
        await using (var ctx = _db.NewContext())
        {
            var recipe = RecipeBuilder.Default("CRONUS Long");
            recipe.Description = new string('d', 1000);
            recipe.Keywords = keywords;
            ctx.Recipes.Add(recipe);
            ctx.Recipes.Add(RecipeBuilder.Default("CRONUS Short"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var one = await tools.SearchAsync(query: "cronus", limit: 0);
        one.Rows.Should().ContainSingle("a limit below one still returns one row");
        one.Rows[0].Description.Should().HaveLength(CookbookTools.MaxSummaryDescriptionLength + 3);
        one.Rows[0].Keywords.Should().Be(keywords, "keywords are served whole");
        (await tools.SearchAsync(query: "cronus", limit: 1000)).Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecipe_lists_files_past_the_budget_by_path_and_fetches_them_by_path()
    {
        var big = new string('x', CookbookTools.MaxRecipeAnswerLength / 2);
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Large module", type: RecipeType.Module)
                .WithFile("Setup.al", big, relativePath: "Setup")
                .WithFile("SetupPage.al", big, relativePath: "Setup/Pages")
                .WithFile("Events.al", "// small", relativePath: "Events")
                .WithFile("README.md", "# root");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var whole = await tools.GetAsync(recipeId);
        whole.Files.Select(f => f.Path).Should().Equal("Setup/Setup.al", "Setup/Pages/SetupPage.al", "Events/Events.al", "README.md");
        whole.Files[0].Content.Should().Be(big);
        whole.Files[1].Content.Should().BeNull("the second large body does not fit beside the first and the recipe's text");
        whole.Files[2].Content.Should().Be("// small", "a later file that still fits is kept");
        whole.Truncated.Should().BeTrue();
        whole.Note.Should().Contain("1 of 4 files").And.Contain("path");

        var one = await tools.GetAsync(recipeId, path: "setup/pages/setuppage.al");
        one.Files.Should().ContainSingle().Which.Content.Should().Be(big);
        one.Truncated.Should().BeFalse();

        (await tools.GetAsync(recipeId, path: "Setup")).Files.Select(f => f.Path)
            .Should().Equal(["Setup/Setup.al", "Setup/Pages/SetupPage.al"], "a folder takes in its subfolders");
        (await tools.GetAsync(recipeId, path: "README.md")).Files.Should().ContainSingle().Which.Content.Should().Be("# root");
        await FluentActions.Awaiting(() => tools.GetAsync(recipeId, path: "Set"))
            .Should().ThrowAsync<McpException>().WithMessage("*no file or folder*");
    }

    [Fact]
    public async Task GetRecipe_cuts_a_file_too_large_for_one_answer_and_reads_on_from_an_offset()
    {
        var huge = string.Concat(Enumerable.Range(0, 70_000).Select(i => (char)('a' + i % 26)));
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Huge").WithFile("Huge.al", huge);
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var first = await tools.GetAsync(recipeId);
        first.Truncated.Should().BeTrue();
        var cut = first.Files[0].Content!.Length;
        huge.Should().StartWith(first.Files[0].Content);
        first.Note.Should().Contain($"cut at character {cut} of 70000").And.Contain($"offset {cut}");

        // A second read is cut too, and its note still counts in the whole file (#1527).
        var middle = await tools.GetAsync(recipeId, path: "Huge.al", offset: cut);
        var reached = cut + middle.Files[0].Content!.Length;
        middle.Files[0].Content.Should().Be(huge[cut..reached]);
        middle.Note.Should().Contain($"cut at character {reached} of 70000").And.Contain($"offset {reached}");

        var rest = await tools.GetAsync(recipeId, path: "Huge.al", offset: reached);
        rest.Files[0].Content.Should().Be(huge[reached..]);
        rest.Truncated.Should().BeFalse();

        await FluentActions.Awaiting(() => tools.GetAsync(recipeId, path: "Huge.al", offset: 80_000))
            .Should().ThrowAsync<McpException>().WithMessage("*offset*");
    }

    [Fact]
    public async Task GetRecipe_throws_McpException_for_unknown_id()
    {
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);

        await FluentActions.Awaiting(() => tools.GetAsync(999_999))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task GetRecipe_does_not_hand_an_agent_a_deleted_recipe()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Withdrawn").WithFile("a.al", "// wrong");
            s.DeletedAt = DateTime.UtcNow;
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        await FluentActions.Awaiting(() => tools.GetAsync(recipeId))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task GetCookbookGuidance_returns_org_text_builtin_type_descriptions_and_a_token()
    {
        // No row yet — built-in copy still surfaces, and we still get a token.
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var defaults = await tools.GetCookbookGuidanceAsync();
            defaults.Guidance.Should().BeEmpty();
            defaults.RecipeTypes.Should().BeEquivalentTo(new[] { "Snippet", "Pattern", "Module" });
            defaults.TypeDescriptions.Should().ContainKey("Pattern");
            defaults.GuidanceToken.Should().NotBeNullOrWhiteSpace(
                "the write tools require a token from this call");
            defaults.GuidanceTokenExpiresInSeconds.Should().BeGreaterThan(0);
        }

        // Saved org guidance comes back verbatim.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "Acme",
                DefaultIdRangeFrom = 50_000,
                DefaultIdRangeTo = 50_999,
                CookbookGuidance = "Use the **ACME** prefix on every object name.",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var withSettings = NewCookbookTools(ctx2);
        var result = await withSettings.GetCookbookGuidanceAsync();
        result.Guidance.Should().Be("Use the **ACME** prefix on every object name.");
    }

    [Fact]
    public async Task SuggestRecipe_creates_pending_row_in_caller_org_when_token_is_valid()
    {
        var userId = await SeedSubmitterAsync();

        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Posting Validation Override",
            Description: "Stub override for posting validation.",
            Keywords: "posting, validation",
            Type: "Pattern",
            Files: new[] { new RecipeFileInputDto("Codeunit.al", "// body", "src") },
            Instructions: "Drop into your project and rename the codeunit.");

        var result = await tools.SuggestAsync(input);

        result.SuggestionId.Should().BeGreaterThan(0);
        result.Message.Should().Contain("/admin/cookbook/suggestions");

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions
            .Include(s => s.Files)
            .FirstAsync(s => s.Id == result.SuggestionId);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Type.Should().Be(RecipeType.Pattern);
        row.OrganizationId.Should().Be(_db.OrgContext.CurrentOrganizationId);
        row.Title.Should().Be("Posting Validation Override");
        row.Files.Should().ContainSingle();
        row.Files[0].FileName.Should().Be("Codeunit.al");
        row.Files[0].RelativePath.Should().Be("src");
        row.Files[0].Content.Should().Be("// body");
        row.SuggestedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_is_missing()
    {
        await SeedSubmitterAsync(userId: 820);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);

        var input = new SuggestRecipeInput(
            GuidanceToken: "",
            Title: "No Token",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("get_cookbook_guidance",
            "the error must name the recovery action so the agent knows what to call next");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_is_tampered()
    {
        await SeedSubmitterAsync(userId: 821);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var realToken = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        // Flip the last character; signed payload must reject.
        var bad = realToken[..^1] + (realToken[^1] == 'A' ? 'B' : 'A');

        var input = new SuggestRecipeInput(
            GuidanceToken: bad,
            Title: "Tampered",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_has_expired()
    {
        await SeedSubmitterAsync(userId: 822);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        // Advance past the token's lifetime.
        _clock.Advance(CookbookTools.GuidanceTokenLifetime + TimeSpan.FromSeconds(1));

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Stale",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_token_issued_for_a_different_organisation()
    {
        await SeedSubmitterAsync(userId: 823);

        // Mint a token under OtherOrgId, then switch back to DefaultOrgId
        // and try to submit. The token's signed org id no longer matches.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        string otherOrgToken;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            otherOrgToken = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        await using var ctx2 = _db.NewContext();
        var defaultTools = NewCookbookTools(ctx2);
        var input = new SuggestRecipeInput(
            GuidanceToken: otherOrgToken,
            Title: "Wrong Org Token",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => defaultTools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("different organisation");
    }

    [Fact]
    public async Task SuggestRecipe_returns_McpException_when_title_blank()
    {
        await SeedSubmitterAsync(userId: 801);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "",
            Description: "Anything",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        (await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Title");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_files_with_slashes_in_filename()
    {
        await SeedSubmitterAsync(userId: 802);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Has Slash",
            Description: "Description",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("sub/x.al", "// nope") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task SuggestRecipe_rejects_unknown_type_string()
    {
        await SeedSubmitterAsync(userId: 810);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Bad Type",
            Description: "Body.",
            Keywords: "",
            Type: "Megamodule",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_round_trips_the_change()
    {
        await SeedSubmitterAsync(userId: 803);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "First Draft",
                Description: "Initial body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v1") }));
            suggestionId = created.SuggestionId;
        }

        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var result = await tools.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: token,
                Title: "Revised Draft",
                Description: "Tightened body.",
                Keywords: "alpha",
                Type: "Pattern",
                Files: new[]
                {
                    new RecipeFileInputDto("a.al", "// v2"),
                    new RecipeFileInputDto("b.al", "// added"),
                }));
            result.SuggestionId.Should().Be(suggestionId);
            result.Message.Should().Contain("Pending");
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == suggestionId);
        row.Title.Should().Be("Revised Draft");
        row.Description.Should().Be("Tightened body.");
        row.Type.Should().Be(RecipeType.Pattern);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Files.Select(f => f.FileName).Should().Equal("a.al", "b.al");
        row.Files[0].Content.Should().Be("// v2");
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_also_rejects_a_missing_guidance_token()
    {
        await SeedSubmitterAsync(userId: 830);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "First Draft",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v1") }));
            suggestionId = created.SuggestionId;
        }

        await using var ctx2 = _db.NewContext();
        var tools2 = NewCookbookTools(ctx2);
        await FluentActions.Awaiting(() => tools2.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: "",
                Title: "Revised",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v2") })))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("get_cookbook_guidance"));
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_returns_McpException_when_caller_is_not_submitter()
    {
        await SeedSubmitterAsync(userId: 804);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "Owned",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// hi") }));
            suggestionId = created.SuggestionId;
        }

        await SeedSubmitterAsync(userId: 805);

        await using var ctx2 = _db.NewContext();
        var tools2 = NewCookbookTools(ctx2);
        var token2 = (await tools2.GetCookbookGuidanceAsync()).GuidanceToken;

        await FluentActions.Awaiting(() => tools2.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: token2,
                Title: "Hijacked",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// hi") })))
            .Should().ThrowAsync<McpException>();
    }

    /// <summary>
    /// Mutable clock that lets the expiry test fast-forward past
    /// <see cref="CookbookTools.GuidanceTokenLifetime"/> without sleeping.
    /// </summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));

    private CookbookTools NewCookbookTools(ALDevToolbox.Data.AppDbContext ctx)
    {
        // None of the tools exercised here reaches GitHub; apply_recipe has its
        // own file, with a fake that answers.
        var client = _db.NewGitHubAppClient(ctx, new UnreachableGitHub());
        var access = _db.NewGitHubAccessService(ctx, client);
        return new CookbookTools(
            new RecipeService(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx), new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            ctx,
            new RecipeSuggestionService(ctx, NullLogger<RecipeSuggestionService>.Instance, _db.OrgContext),
            _db.OrgContext,
            _db.DataProtectionProvider,
            _clock,
            _db.NewGitHubRecipeDeliveryService(ctx, client, access));
    }

    private sealed class UnreachableGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("api.github.com is not reachable from tests.");
    }

    [Fact]
    public async Task UpdateRecipe_rejects_caller_without_editor_role()
    {
        await SeedSubmitterAsync(userId: 830); // Role = User

        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var recipe = RecipeBuilder.Default("Locked Down").WithFile("A.al", "// a");
            ctx.Recipes.Add(recipe);
            await ctx.SaveChangesAsync();
            recipeId = recipe.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(new UpdateRecipeInput(
                RecipeId: recipeId,
                GuidanceToken: token,
                Title: "Locked Down",
                Description: "New body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("A.al", "// edited") })))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("Editor or Admin");

        await using var verify = _db.NewContext();
        (await verify.RecipeFiles.SingleAsync(f => f.RecipeId == recipeId))
            .Content.Should().Be("// a", "the edit must not land without the role");
    }

    [Fact]
    public async Task UpdateRecipe_replaces_fields_and_preserves_deprecated_when_omitted()
    {
        var userId = await SeedSubmitterAsync(userId: 831);
        await using (var ctx = _db.NewContext())
        {
            var user = await ctx.Users.SingleAsync(u => u.Id == userId);
            user.Role = UserRole.Editor;
            await ctx.SaveChangesAsync();
        }

        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var recipe = RecipeBuilder.Default("Editable").WithFile("A.al", "// a");
            recipe.Deprecated = true;
            ctx.Recipes.Add(recipe);
            await ctx.SaveChangesAsync();
            recipeId = recipe.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var result = await tools.UpdateRecipeAsync(new UpdateRecipeInput(
            RecipeId: recipeId,
            GuidanceToken: token,
            Title: "Editable v2",
            Description: "Refreshed.",
            Keywords: "alpha",
            Type: "Pattern",
            Files: new[] { new RecipeFileInputDto("B.al", "// b", "src") },
            EstimatedValueHours: 3.25m));

        result.RecipeId.Should().Be(recipeId);

        await using var verify = _db.NewContext();
        var row = await verify.Recipes.Include(r => r.Files).SingleAsync(r => r.Id == recipeId);
        row.Title.Should().Be("Editable v2");
        row.Type.Should().Be(RecipeType.Pattern);
        row.EstimatedValueHours.Should().Be(3.25m);
        row.Deprecated.Should().BeTrue("an omitted Deprecated must keep the current flag");
        row.Files.Should().ContainSingle().Which.FileName.Should().Be("B.al");
    }

    // #1626: get_recipe returns the minimum version as a string, never its id,
    // and lists files that did not fit with a null body. A round-trip through
    // update_recipe must not drop either.

    private async Task<(int RecipeId, int VersionId)> SeedVersionGatedRecipeAsync(int userId)
    {
        var editorId = await SeedSubmitterAsync(userId);
        await using var ctx = _db.NewContext();
        var user = await ctx.Users.SingleAsync(u => u.Id == editorId);
        user.Role = UserRole.Editor;
        var version = NewApplicationVersion("bc-24", "Business Central 24", "24.0.0.0");
        ctx.ApplicationVersions.Add(version);
        ctx.ApplicationVersions.Add(NewApplicationVersion("bc-25", "Business Central 25", "25.0.0.0"));
        await ctx.SaveChangesAsync();

        var recipe = RecipeBuilder.Default("Gated")
            .WithFile("A.al", "// a")
            .WithFile("B.al", "// b body", "src");
        recipe.MinimumApplicationVersionId = version.Id;
        recipe.EstimatedValueHours = 4m;
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return (recipe.Id, version.Id);
    }

    private static ApplicationVersion NewApplicationVersion(string key, string name, string application) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Key = key,
        Name = name,
        Application = application,
        Runtime = application[..4],
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static UpdateRecipeInput GatedEdit(int recipeId, string token, IReadOnlyList<RecipeFileInputDto>? files = null) => new(
        RecipeId: recipeId,
        GuidanceToken: token,
        Title: "Gated",
        Description: "Typo fixed.",
        Keywords: "",
        Type: "Snippet",
        Files: files ?? new[] { new RecipeFileInputDto("A.al", "// a edited"), new RecipeFileInputDto("B.al", "// b body", "src") });

    // #1547: suggest_recipe and update_recipe_suggestion took only the version id,
    // which no tool returns. They now take the same MinimumApplication string.

    private async Task SeedVersionsAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.ApplicationVersions.Add(NewApplicationVersion("bc-24", "Business Central 24", "24.0.0.0"));
        ctx.ApplicationVersions.Add(NewApplicationVersion("bc-25", "Business Central 25", "25.0.0.0"));
        await ctx.SaveChangesAsync();
    }

    private static SuggestRecipeInput VersionedSuggestion(string token, string? minimumApplication) => new(
        GuidanceToken: token,
        Title: "Versioned",
        Description: "Needs a recent base app.",
        Keywords: "",
        Type: "Snippet",
        Files: new[] { new RecipeFileInputDto("a.al", "// a") },
        MinimumApplication: minimumApplication);

    [Fact]
    public async Task SuggestRecipe_takes_the_minimum_version_as_an_application_version_string()
    {
        await SeedSubmitterAsync(userId: 842);
        await SeedVersionsAsync();
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var id = (await tools.SuggestAsync(VersionedSuggestion(token, "24.0.0.0"))).SuggestionId;

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions.Include(s => s.MinimumApplicationVersion).SingleAsync(s => s.Id == id);
        row.MinimumApplicationVersion!.Key.Should().Be("bc-24");
    }

    [Fact]
    public async Task SuggestRecipe_refuses_a_version_the_organisation_does_not_have_and_stores_nothing()
    {
        await SeedSubmitterAsync(userId: 843);
        await SeedVersionsAsync();
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.SuggestAsync(VersionedSuggestion(token, "99.0.0.0")))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("99.0.0.0");

        await using var verify = _db.NewContext();
        (await verify.RecipeSuggestions.AnyAsync(s => s.Title == "Versioned")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_sets_and_clears_the_minimum_version_by_name()
    {
        await SeedSubmitterAsync(userId: 844);
        await SeedVersionsAsync();
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        var id = (await tools.SuggestAsync(VersionedSuggestion(token, null))).SuggestionId;

        UpdateRecipeSuggestionInput Edit(string? minimum) => new(
            SuggestionId: id, GuidanceToken: token, Title: "Versioned", Description: "Needs a recent base app.",
            Keywords: "", Type: "Snippet", Files: new[] { new RecipeFileInputDto("a.al", "// a") },
            MinimumApplication: minimum);

        await tools.UpdateSuggestionAsync(Edit("business central 25"));
        await using (var verify = _db.NewContext())
        {
            var row = await verify.RecipeSuggestions.Include(s => s.MinimumApplicationVersion).SingleAsync(s => s.Id == id);
            row.MinimumApplicationVersion!.Application.Should().Be("25.0.0.0");
        }

        await tools.UpdateSuggestionAsync(Edit(""));
        await using (var verify = _db.NewContext())
        {
            (await verify.RecipeSuggestions.SingleAsync(s => s.Id == id)).MinimumApplicationVersionId.Should().BeNull();
        }
    }

    [Fact]
    public async Task UpdateRecipe_keeps_minimum_version_and_hours_when_omitted()
    {
        var (recipeId, versionId) = await SeedVersionGatedRecipeAsync(userId: 834);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token));

        await using var verify = _db.NewContext();
        var row = await verify.Recipes.SingleAsync(r => r.Id == recipeId);
        row.Description.Should().Be("Typo fixed.");
        row.MinimumApplicationVersionId.Should().Be(versionId, "an omitted minimum version must keep the stored one");
        row.EstimatedValueHours.Should().Be(4m, "an omitted EstimatedValueHours must keep the stored value");
    }

    [Fact]
    public async Task UpdateRecipe_round_trips_what_get_recipe_returns()
    {
        var (recipeId, versionId) = await SeedVersionGatedRecipeAsync(userId: 835);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        var detail = await tools.GetAsync(recipeId);

        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with
        {
            MinimumApplication = detail.MinimumApplication,
            EstimatedValueHours = detail.EstimatedValueHours,
        });

        await using var verify = _db.NewContext();
        var row = await verify.Recipes.SingleAsync(r => r.Id == recipeId);
        row.MinimumApplicationVersionId.Should().Be(versionId);
        row.EstimatedValueHours.Should().Be(4m);
    }

    [Fact]
    public async Task UpdateRecipe_changes_and_clears_minimum_version_and_hours_explicitly()
    {
        var (recipeId, _) = await SeedVersionGatedRecipeAsync(userId: 836);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with { MinimumApplication = "Business Central 25", EstimatedValueHours = 6m });
        await using (var verify = _db.NewContext())
        {
            var row = await verify.Recipes.Include(r => r.MinimumApplicationVersion).SingleAsync(r => r.Id == recipeId);
            row.MinimumApplicationVersion!.Application.Should().Be("25.0.0.0");
            row.EstimatedValueHours.Should().Be(6m);
        }

        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with { MinimumApplication = "", ClearEstimatedValueHours = true });
        await using (var verify = _db.NewContext())
        {
            var row = await verify.Recipes.SingleAsync(r => r.Id == recipeId);
            row.MinimumApplicationVersionId.Should().BeNull("an empty MinimumApplication removes the requirement");
            row.EstimatedValueHours.Should().BeNull("ClearEstimatedValueHours removes the value");
        }
    }

    [Fact]
    public async Task UpdateRecipe_refuses_an_unknown_minimum_version_and_keeps_the_recipe()
    {
        var (recipeId, versionId) = await SeedVersionGatedRecipeAsync(userId: 837);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with { MinimumApplication = "99.0.0.0" }))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("99.0.0.0");

        await using var verify = _db.NewContext();
        var row = await verify.Recipes.SingleAsync(r => r.Id == recipeId);
        row.MinimumApplicationVersionId.Should().Be(versionId);
        row.Description.Should().NotBe("Typo fixed.");
    }

    [Fact]
    public async Task UpdateRecipe_names_MinimumApplication_when_the_stored_version_was_removed()
    {
        var (recipeId, versionId) = await SeedVersionGatedRecipeAsync(userId: 840);
        await using (var seed = _db.NewContext())
        {
            var version = await seed.ApplicationVersions.SingleAsync(a => a.Id == versionId);
            version.DeletedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(GatedEdit(recipeId, token)))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("MinimumApplication").And.Contain("Business Central 24");

        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with { MinimumApplication = "" });
        await using var verify = _db.NewContext();
        var row = await verify.Recipes.SingleAsync(r => r.Id == recipeId);
        row.MinimumApplicationVersionId.Should().BeNull();
        row.Description.Should().Be("Typo fixed.");
    }

    [Fact]
    public async Task UpdateRecipe_offers_the_keys_when_a_minimum_version_is_ambiguous()
    {
        var (recipeId, _) = await SeedVersionGatedRecipeAsync(userId: 841);
        await using (var seed = _db.NewContext())
        {
            seed.ApplicationVersions.Add(NewApplicationVersion("bc-25-alt", "Business Central 25 (alt)", "25.0.0.0"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(GatedEdit(recipeId, token) with { MinimumApplication = "25.0.0.0" }))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("'bc-25'").And.Contain("'bc-25-alt'").And.Contain("key");
    }

    [Fact]
    public async Task UpdateRecipe_keeps_the_stored_body_of_a_file_sent_with_null_content()
    {
        var (recipeId, _) = await SeedVersionGatedRecipeAsync(userId: 838);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        // As get_recipe lists a file it left out of its answer: path known, body null.
        await tools.UpdateRecipeAsync(GatedEdit(recipeId, token, new[]
        {
            new RecipeFileInputDto("A.al", "// a edited"),
            new RecipeFileInputDto("b.al", null, "SRC/"),
        }));

        await using var verify = _db.NewContext();
        var files = await verify.RecipeFiles.Where(f => f.RecipeId == recipeId).OrderBy(f => f.Ordering).ToListAsync();
        files.Select(f => f.Content).Should().Equal("// a edited", "// b body");
    }

    [Fact]
    public async Task UpdateRecipe_refuses_null_content_for_a_file_the_recipe_does_not_have()
    {
        var (recipeId, _) = await SeedVersionGatedRecipeAsync(userId: 839);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(GatedEdit(recipeId, token, new[]
            {
                new RecipeFileInputDto("A.al", "// a"),
                new RecipeFileInputDto("New.al", null),
            })))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("New.al");
    }

    [Fact]
    public async Task UpdateRecipe_rejects_when_guidance_token_is_missing()
    {
        await SeedSubmitterAsync(userId: 832);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);

        var ex = await FluentActions.Awaiting(() => tools.UpdateRecipeAsync(new UpdateRecipeInput(
                RecipeId: 1,
                GuidanceToken: "",
                Title: "X",
                Description: "Y.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// hi") })))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("get_cookbook_guidance");
    }

    [Fact]
    public async Task SuggestRecipe_carries_estimated_value_hours()
    {
        await SeedSubmitterAsync(userId: 833);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var result = await tools.SuggestAsync(new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Hours Carrier",
            Description: "Worth something.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") },
            EstimatedValueHours: 12m));

        await using var verify = _db.NewContext();
        (await verify.RecipeSuggestions.SingleAsync(s => s.Id == result.SuggestionId))
            .EstimatedValueHours.Should().Be(12m);
    }

    // ---- WorkspaceTools ----------------------------------------------------

    [Fact]
    public async Task ListTemplates_excludes_deprecated_by_default()
    {
        await using (var ctx = _db.NewContext())
        {
            var activeTpl = TemplateBuilder.Default("runtime-15");
            activeTpl.IsDefault = true;
            var deprecated = TemplateBuilder.Default("runtime-old");
            deprecated.Deprecated = true;
            ctx.RuntimeTemplates.Add(activeTpl);
            ctx.RuntimeTemplates.Add(deprecated);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var templates = new TemplateService(ctx2, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx2));
        var modules = new ModuleService(ctx2, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx2));
        var catalog = new CatalogService(ctx2, NullLogger<CatalogService>.Instance, _db.OrgContext);
        var tools = new WorkspaceTools(templates, modules, catalog, generation: null!, delivery: null!, repositories: null!, _downloads, _db.OrgContext, Options);

        var active = await tools.ListTemplatesAsync(includeDeprecated: false);
        active.Select(t => t.Key).Should().Equal("runtime-15");

        var all = await tools.ListTemplatesAsync(includeDeprecated: true);
        all.Select(t => t.Key).Should().BeEquivalentTo(new[] { "runtime-15", "runtime-old" });
    }

    [Fact]
    public async Task GetTemplate_names_the_optional_extensions_generate_workspace_accepts()
    {
        // #792: the whole point. selectedExtensionPaths took a bare list of
        // paths that nothing in the read surface listed, so an agent had to
        // guess or generate once and read the ZIP back.
        await using (var ctx = _db.NewContext())
        {
            var tpl = TemplateBuilder.Default("runtime-15");
            tpl.WorkspaceExtensions.Add(new WorkspaceExtension
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = "Hotfix",
                NameTemplate = "{{extension_prefix}} Hotfix",
                Required = false,
                Ordering = 1,
                Runtime = "14",
            });
            tpl.RootFolders.Add(new RuntimeTemplateRootFolder
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = ".alpackages",
                Ordering = 0,
            });
            ctx.RuntimeTemplates.Add(tpl);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var detail = await NewWorkspaceTools(ctx2).GetTemplateAsync("runtime-15");

        detail.Key.Should().Be("runtime-15");
        detail.Extensions.Select(e => e.Path).Should().Equal("Core", "Hotfix");

        var core = detail.Extensions.Single(e => e.Path == "Core");
        core.Required.Should().BeTrue("required extensions are generated whether or not they are named");
        core.Runtime.Should().BeNull("Core does not override the template's runtime");

        var hotfix = detail.Extensions.Single(e => e.Path == "Hotfix");
        hotfix.Required.Should().BeFalse("this is the one an agent could not previously discover");
        hotfix.NameTemplate.Should().Be("{{extension_prefix}} Hotfix");
        hotfix.Runtime.Should().Be("14", "per-extension overrides are part of what the caller needs");

        detail.RootFolders.Should().Equal(".alpackages");
        detail.Defaults.Publisher.Should().Be("Acme");
        detail.Defaults.AffixType.Should().Be("Prefix", "enums cross the wire as their names");
    }

    [Fact]
    public async Task GetTemplate_refuses_a_key_list_templates_would_not_show()
    {
        await using (var ctx = _db.NewContext())
        {
            var deleted = TemplateBuilder.Default("runtime-gone");
            deleted.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            ctx.RuntimeTemplates.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewWorkspaceTools(ctx2);

        // GetByKeyAsync does not filter soft-deleted rows, so without the
        // tool's own guard this would answer for a template list_templates hides.
        var soft = async () => await tools.GetTemplateAsync("runtime-gone");
        await soft.Should().ThrowAsync<McpException>();

        var missing = async () => await tools.GetTemplateAsync("no-such-template");
        await missing.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task GetTemplate_drops_a_default_module_that_has_been_deleted()
    {
        await using (var ctx = _db.NewContext())
        {
            var live = new Module
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "live", Name = "Live", ExtensionName = "Live",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var gone = new Module
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "gone", Name = "Gone", ExtensionName = "Gone",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            ctx.Modules.AddRange(live, gone);

            var tpl = TemplateBuilder.Default("runtime-15");
            tpl.DefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = TestDb.DefaultOrgId, Module = live, Ordering = 0,
            });
            tpl.DefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = TestDb.DefaultOrgId, Module = gone, Ordering = 1,
            });
            ctx.RuntimeTemplates.Add(tpl);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var detail = await NewWorkspaceTools(ctx2).GetTemplateAsync("runtime-15");

        detail.DefaultModules.Select(m => m.Key).Should().Equal(
            new[] { "live" },
            "New Workspace drops soft-deleted modules when it pre-selects this set, so a key the form "
            + "would never tick is not one to hand an agent");
    }

    [Fact]
    public async Task ListModules_carries_the_extension_name_each_module_produces()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Modules.Add(new Module
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "posting",
                Name = "Posting",
                ExtensionName = "Posting Engine",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var modules = await NewWorkspaceTools(ctx2).ListModulesAsync();

        modules.Single(m => m.Key == "posting").ExtensionName.Should().Be(
            "Posting Engine", "the key alone does not say what folder the module lands in");
    }

    [Fact]
    public async Task GenerateWorkspace_returns_a_download_link_to_the_zip_with_its_sha256()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        var result = await tools.GenerateWorkspaceAsync(new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "Acme Customer",
            ExtensionPrefix: "ACME",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999));

        result.FileName.Should().EndWith(".zip");
        result.DownloadPath.Should().StartWith(GeneratedDownloadStore.PathPrefix);
        result.DownloadExpiresAt.Should().BeAfter(DateTime.UtcNow);
        result.SizeBytes.Should().BeGreaterThan(0);
        result.Sha256.Should().HaveLength(64);

        // Round-trip: link → bytes → ZipArchive → recognisable entry.
        var bytes = Downloaded(result);
        bytes.Length.Should().Be(result.SizeBytes);
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(result.Sha256);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName.EndsWith(".code-workspace"));
    }

    [Fact]
    public async Task GenerateWorkspace_says_so_when_the_download_store_is_full()
    {
        await SeedDefaultTemplateAsync();
        using var full = new GeneratedDownloadStore(TimeProvider.System, maxHeldBytes: 1);

        await using var ctx = _db.NewContext();
        var act = () => NewWorkspaceTools(ctx, downloads: full).GenerateWorkspaceAsync(new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "CRONUS Customer",
            ExtensionPrefix: "CRN",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999));

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("too many generated archives");
    }

    [Fact]
    public async Task GenerateWorkspace_uses_the_templates_default_modules_when_selectedModuleKeys_is_left_out()
    {
        // #1243: the parameter's description promises the template's default
        // modules; leaving it out used to generate none. A soft-deleted
        // default drops out, as it does from get_template.
        await using (var seed = _db.NewContext())
        {
            var banking = ModuleBuilder.Default("banking", "Banking")
                .WithExtensionFolder("src", ("Bank.Codeunit.al", "codeunit 91000 Bank { }"));
            var retired = ModuleBuilder.Default("retired", "Retired")
                .WithExtensionFolder("src", ("Old.Codeunit.al", "codeunit 91200 Old { }"));
            retired.DeletedAt = DateTime.UtcNow;
            var template = TemplateBuilder.Default("runtime-test");
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = TestDb.DefaultOrgId, Module = banking, Ordering = 0,
            });
            template.DefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = TestDb.DefaultOrgId, Module = retired, Ordering = 1,
            });
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        ProjectPlanInput Input(IReadOnlyList<string>? modules) => new(
            TemplateKey: "runtime-test",
            WorkspaceName: "CRONUS A/S",
            ExtensionPrefix: "CRO",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999,
            SelectedModuleKeys: modules);

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        var leftOut = await tools.GenerateWorkspaceAsync(Input(null));
        using (var zip = new ZipArchive(new MemoryStream(Downloaded(leftOut)), ZipArchiveMode.Read))
        {
            zip.Entries.Should().Contain(e => e.FullName == "CRONUSAS/Banking/src/Bank.Codeunit.al");
            zip.Entries.Should().NotContain(e => e.FullName.Contains("/Retired/"));
        }

        var none = await tools.GenerateWorkspaceAsync(Input(Array.Empty<string>()));
        using (var zip = new ZipArchive(new MemoryStream(Downloaded(none)), ZipArchiveMode.Read))
        {
            zip.Entries.Should().NotContain(e => e.FullName.Contains("/Banking/"),
                "an empty list is an agent asking for no modules");
        }
    }

    [Fact]
    public async Task GenerateWorkspace_carries_the_short_name_into_the_plan()
    {
        // MCP parity (PROJECT.md): what the New Workspace form can say, the
        // tool can say. The short name reaches the plan, so an agent's
        // workspace is named the way a consultant's would be.
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        var input = new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "CRONUS A/S",
            ExtensionPrefix: "CRO",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999,
            ShortName: "CRO");

        input.ToDomain().ShortName.Should().Be("CRO");
        input.ToDomain().EffectiveShortName.Should().Be("CRO");

        var result = await tools.GenerateWorkspaceAsync(input);
        result.FileName.Should().Be("CRONUSAS.zip", "the folder still comes from the customer's name");
    }

    [Fact]
    public async Task GenerateWorkspace_ignores_the_prefix_an_agent_passes_when_the_organisation_fixes_one()
    {
        // MCP parity (PROJECT.md): the prefix policy is the organisation's, so
        // the tool obeys it exactly as the form does - and reports back the
        // prefix the extensions actually carry (#757).
        await SeedDefaultTemplateAsync();
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                ExtensionPrefixMode = ALDevToolbox.Domain.ValueObjects.ExtensionPrefixMode.Fixed,
                ExtensionPrefix = "PARTNER",
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        var result = await tools.GenerateWorkspaceAsync(new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "CRONUS A/S",
            ExtensionPrefix: "TYPED",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999));

        // What the extensions are called, not what the agent asked for. The
        // generated names themselves are covered end to end in
        // WorkspaceGenerationTests; here the point is that the tool reports the
        // prefix that won.
        result.ExtensionPrefix.Should().Be("PARTNER");
    }

    [Fact]
    public async Task GenerateWorkspace_surfaces_PlanValidationException_as_McpException()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        // Empty workspace name fails validation in GenerationService.
        var input = new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "   ",
            ExtensionPrefix: "ACME",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999);

        await FluentActions.Awaiting(() => tools.GenerateWorkspaceAsync(input))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("Validation failed"));
    }

    [Fact]
    public async Task GenerateWorkspace_refuses_payloads_over_the_size_cap()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        // 1-byte cap means every real generation trips the guard.
        var tinyOptions = Microsoft.Extensions.Options.Options.Create(new McpOptions { MaxWorkspaceBytes = 1 });
        var tools = NewWorkspaceTools(ctx, tinyOptions);

        await FluentActions.Awaiting(() => tools.GenerateWorkspaceAsync(new ProjectPlanInput(
                TemplateKey: "runtime-test",
                WorkspaceName: "Acme Customer",
                ExtensionPrefix: "ACME",
                Brief: "Brief",
                Description: "Description",
                ApplicationVersion: "24.0.0.0",
                RuntimeVersion: "15",
                CoreIdRangeFrom: 90000,
                CoreIdRangeTo: 90999)))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("more than the 1 this server hands out"));
    }

    // ---- ObjectExplorerTools ----------------------------------------------

    private static readonly string OeFixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private Task<int> SeedDkCoreReleaseAsync(bool storeSymbolReference = false, string label = "MCP DK Core") =>
        SeedOeReleaseAsync("Microsoft_DK_Core.app", label, storeSymbolReference);

    private async Task<int> SeedOeReleaseAsync(string appFileName, string label, bool storeSymbolReference = false)
    {
        await using var ctx = _db.NewContext();
        var importer = new ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new ALDevToolbox.Services.ObjectExplorer.Import.TranslationImportService(
                ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<ALDevToolbox.Services.ObjectExplorer.Import.TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new ALDevToolbox.Services.ObjectExplorer.Import.CallSiteReferenceEmitter(ctx, NullLogger<ALDevToolbox.Services.ObjectExplorer.Import.CallSiteReferenceEmitter>.Instance),
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));
        await using var s1 = File.OpenRead(Path.Combine(OeFixtureRoot, appFileName));
        var summary = await importer.ImportReleaseAsync(
            new ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportRequest(
                Label: label,
                Kind: "first_party",
                ParentReleaseId: null,
                ApplicationVersionId: null,
                Uploads: new[]
                {
                    new ALDevToolbox.Services.ObjectExplorer.Import.AppFileUpload("dk.app", s1, SourceZipStream: null),
                },
                StoreSymbolReference: storeSymbolReference));
        return summary.ReleaseId;
    }

    private ObjectExplorerTools NewOeTools(Data.AppDbContext ctx)
    {
        var access = new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext);
        var references = new ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceQueryService(
            ctx, access, _db.OrgContext, NullLogger<ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceQueryService>.Instance);
        var explorer = new ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService(
            ctx, references, access, NullLogger<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService>.Instance);
        var search = new ALDevToolbox.Services.ObjectExplorer.Explore.ObjectSearchService(ctx, access);
        var translations = new ALDevToolbox.Services.ObjectExplorer.Explore.TranslationQueryService(ctx);
        var comparison = new ALDevToolbox.Services.ObjectExplorer.Explore.ReleaseComparisonService(
            ctx, access, NullLogger<ALDevToolbox.Services.ObjectExplorer.Explore.ReleaseComparisonService>.Instance);
        return new ObjectExplorerTools(explorer, search, references, translations, comparison);
    }

    /// <summary>
    /// The two fixture apps carry different AppIds, so every file in one
    /// release is "removed" and every file in the other is "added" — the
    /// wholesale add/remove case the object-level compare_releases cannot see
    /// at all. See issue #578.
    /// </summary>
    [Fact]
    public async Task CompareReleaseFiles_reports_whole_files_added_and_removed()
    {
        var leftId = await SeedOeReleaseAsync("Microsoft_DK_Core.app", "MCP compare files left");
        var rightId = await SeedOeReleaseAsync("Microsoft_OIOUBL.app", "MCP compare files right");
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        var result = await tools.CompareReleaseFilesAsync(leftId.ToString(), rightId.ToString());
        var rows = result.Rows;

        rows.Should().NotBeEmpty();
        rows.Count.Should().BeLessThanOrEqualTo(200, "the tool caps its response like its siblings");
        rows.Should().OnlyContain(r => r.Status == "added" || r.Status == "removed",
            "no file can be 'modified' when the two releases share no app");
        rows.Where(r => r.Status == "added").Should()
            .OnlyContain(r => r.LeftFileId == null && r.RightFileId != null);
        rows.Where(r => r.Status == "removed").Should()
            .OnlyContain(r => r.LeftFileId != null && r.RightFileId == null);

        // A short list must never be silently short: whenever the tool cut the
        // set it says so, and says how to narrow the question (#685).
        if (result.Truncated)
        {
            result.Note.Should().NotBeNullOrWhiteSpace();
            result.Note.Should().Contain("narrow");
        }
        else
        {
            result.Note.Should().BeNull();
        }
    }

    [Fact]
    public async Task CompareReleaseFiles_pathPattern_narrows_to_matching_files()
    {
        var leftId = await SeedOeReleaseAsync("Microsoft_DK_Core.app", "MCP compare files filter left");
        var rightId = await SeedOeReleaseAsync("Microsoft_OIOUBL.app", "MCP compare files filter right");
        await using var ctx = _db.NewContext();

        // Pick a real file from the right-hand release so the filter has a
        // path that definitely produces a row, without pinning the test to a
        // fixture filename.
        var target = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == rightId)
            .Select(f => new { f.Id, f.Path })
            .FirstAsync();
        var tools = NewOeTools(ctx);

        var rows = (await tools.CompareReleaseFilesAsync(
            leftId.ToString(), rightId.ToString(), pathPattern: target.Path)).Rows;

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Path.Contains(target.Path, StringComparison.OrdinalIgnoreCase));
        rows.Should().Contain(r => r.RightFileId == target.Id && r.Status == "added");
    }

    [Fact]
    public async Task CompareReleaseFiles_throws_McpException_for_unknown_release()
    {
        var releaseId = await SeedDkCoreReleaseAsync(label: "MCP compare files unknown");
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        await FluentActions.Awaiting(() => tools.CompareReleaseFilesAsync(
                releaseId.ToString(), "No Such Release"))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("list_releases"));
    }

    [Fact]
    public async Task ListReleaseModules_returns_modules_with_symbol_reference_flag()
    {
        var releaseId = await SeedDkCoreReleaseAsync(storeSymbolReference: true, label: "MCP list modules");
        await using var ctx = _db.NewContext();
        var module = await ctx.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Name })
            .SingleAsync();
        var tools = NewOeTools(ctx);

        var result = await tools.ListReleaseModulesAsync(releaseId.ToString());

        result.Truncated.Should().BeFalse();
        result.Note.Should().BeNull();
        var row = result.Rows.Should().ContainSingle().Subject;
        row.Name.Should().Be(module.Name);
        row.HasStoredSymbolReference.Should().BeTrue("the seed imported with storeSymbolReference: true");
    }

    [Fact]
    public async Task ListReleaseModules_marks_modules_without_stored_symbol_reference()
    {
        var releaseId = await SeedDkCoreReleaseAsync(label: "MCP list modules nosym");
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        var rows = (await tools.ListReleaseModulesAsync(releaseId.ToString())).Rows;

        rows.Should().ContainSingle()
            .Which.HasStoredSymbolReference.Should().BeFalse();
    }

    [Fact]
    public async Task ListReleaseModules_says_when_modules_past_the_cap_were_left_out()
    {
        // A full Business Central release has more modules than the cap, and the
        // ones late in the alphabet used to vanish without a word (#1344, MCP-3).
        var releaseId = await SeedDkCoreReleaseAsync(label: "MCP list modules cap");
        await using (var seed = _db.NewContext())
        {
            for (var i = 0; i < 201; i++)
            {
                seed.OeModules.Add(new OeModule
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    ReleaseId = releaseId,
                    AppId = Guid.NewGuid(),
                    Name = $"Bulk Module {i:D3}",
                    Publisher = "CRONUS",
                    Version = "1.0.0.0",
                    CreatedAt = DateTime.UtcNow,
                });
            }
            seed.OeModules.Add(new OeModule
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = releaseId,
                AppId = Guid.NewGuid(),
                Name = "Zz System Application",
                Publisher = "CRONUS",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        var all = await tools.ListReleaseModulesAsync(releaseId.ToString());
        all.Rows.Should().HaveCount(100);
        all.Truncated.Should().BeTrue();
        all.Note.Should().Contain("namePattern");
        all.Rows.Should().NotContain(r => r.Name == "Zz System Application");

        var narrowed = await tools.ListReleaseModulesAsync(releaseId.ToString(), namePattern: "system app");
        narrowed.Truncated.Should().BeFalse();
        narrowed.Rows.Should().ContainSingle().Which.Name.Should().Be("Zz System Application");
    }

    [Fact]
    public async Task ListReleases_shows_what_the_releases_page_shows()
    {
        // A failed or importing release cannot be searched, and the empty symbol
        // packages builds pull would grow the list without bound (#1344, MCP-8).
        await using (var seed = _db.NewContext())
        {
            seed.OeReleases.AddRange(
                NewRelease("MCP list ready", "ready"),
                NewRelease("MCP list failed", "failed"),
                NewRelease("MCP list importing", "importing"),
                NewRelease("MCP list empty symbols", "ready",
                    dedupKey: OeRelease.SymbolFeedDedupPrefix + "cronus-empty"));
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        var labels = (await NewOeTools(ctx).ListReleasesAsync()).Rows.Select(r => r.Label).ToList();

        labels.Should().Contain("MCP list ready");
        labels.Should().NotContain(new[] { "MCP list failed", "MCP list importing", "MCP list empty symbols" });
    }

    [Fact]
    public async Task A_release_label_shared_with_a_failed_import_resolves_to_the_ready_release()
    {
        int readyId;
        await using (var seed = _db.NewContext())
        {
            var ready = NewRelease("MCP shared label", "ready");
            seed.OeReleases.Add(ready);
            await seed.SaveChangesAsync();
            readyId = ready.Id;
            // The retry that failed comes after the one that worked, so "newest"
            // alone would pick the wrong one.
            seed.OeReleases.Add(NewRelease("MCP shared label", "failed"));
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var explorer = new ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService(
            ctx,
            new ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceQueryService(
                ctx, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext), _db.OrgContext,
                NullLogger<ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceQueryService>.Instance),
            new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService>.Instance);

        (await explorer.ResolveReleaseAsync("MCP shared label")).Should().Be(readyId);
    }

    private static OeRelease NewRelease(string label, string status, string? dedupKey = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        Kind = "first_party",
        Status = status,
        DedupKey = dedupKey,
        ImportedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task DownloadSymbolReference_returns_link_to_streaming_endpoint()
    {
        var releaseId = await SeedDkCoreReleaseAsync(storeSymbolReference: true, label: "MCP DK Core symbols");
        await using var ctx = _db.NewContext();
        var module = await ctx.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.Name })
            .SingleAsync();
        var tools = NewOeTools(ctx);

        var result = await tools.DownloadSymbolReferenceAsync(releaseId.ToString(), module.Name);

        result.ModuleName.Should().Be(module.Name);
        result.ContentHash.Should().NotBeNullOrEmpty();
        result.ContentLength.Should().BeGreaterThan(0);
        result.DownloadPath.Should().Be($"/api/object-explorer/release/{releaseId}/modules/{module.Id}/symbol-reference");
    }

    [Fact]
    public async Task DownloadSymbolReference_throws_when_not_stored()
    {
        var releaseId = await SeedDkCoreReleaseAsync(label: "MCP DK Core nosym");
        await using var ctx = _db.NewContext();
        var moduleName = await ctx.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => m.Name)
            .SingleAsync();
        var tools = NewOeTools(ctx);

        var act = async () => await tools.DownloadSymbolReferenceAsync(releaseId.ToString(), moduleName);
        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*no stored SymbolReference*");
    }

    [Fact]
    public async Task GetObjectOutline_returns_symbol_rows_with_ids_and_line_numbers()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Pick any codeunit in DK Core to outline. We discover the name
        // via the same path the agent would — search_objects.
        var found = (await tools.SearchObjectsAsync(releaseId.ToString(), namePattern: "", kind: "codeunit")).Rows;
        found.Should().NotBeEmpty(because: "DK Core ships at least one codeunit");
        var target = found.First();

        var outline = (await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit")).Outline;
        outline.Name.Should().Be(target.Name);
        outline.Symbols.Should().NotBeEmpty(
            because: "every imported codeunit has at least one procedure / trigger row");
        outline.Symbols.Should().AllSatisfy(s => s.Id.Should().BeGreaterThan(0));
        outline.Symbols.Should().BeInAscendingOrder(s => s.LineNumber);
    }

    [Fact]
    public async Task GetObjectOutline_narrows_members_by_kind_and_name()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);
        var target = (await tools.SearchObjectsAsync(releaseId.ToString(), namePattern: "", kind: "codeunit")).Rows.First();

        var all = await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit");
        all.TotalSymbols.Should().Be(all.Outline.Symbols.Count);
        all.Truncated.Should().BeFalse();

        var procedures = await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit", memberKind: "procedure");
        procedures.Outline.Symbols.Should().OnlyContain(sym => sym.Kind.Contains("procedure"));

        var first = all.Outline.Symbols[0];
        var named = await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit", namePattern: first.Name.ToUpperInvariant());
        named.Outline.Symbols.Should().Contain(sym => sym.Id == first.Id);
        named.Outline.Symbols.Should().OnlyContain(sym => sym.Name.Contains(first.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_outline_past_the_cap_keeps_the_first_members_and_says_how_many_matched()
    {
        var symbols = Enumerable.Range(1, 5)
            .Select(i => new ObjectSymbolRow(i, "procedure", $"Post{i}", null, null, null, i))
            .ToList();
        var outline = new ObjectOutline(1, "codeunit", 80, "CRONUS Post", 1, "CRONUS", null, null, 1, symbols);

        var result = ObjectOutlineResult.From(outline, memberKind: null, namePattern: null, cap: 3);

        result.Outline.Symbols.Select(sym => sym.Name).Should().Equal("Post1", "Post2", "Post3");
        result.TotalSymbols.Should().Be(5);
        result.Truncated.Should().BeTrue();
        result.Note.Should().Contain("first 3 of 5").And.Contain("memberKind");
    }

    [Fact]
    public void An_outline_keeps_every_member_kind_named_in_a_comma_separated_list()
    {
        var symbols = new List<ObjectSymbolRow>
        {
            new(1, "field", "No.", null, null, null, 1),
            new(2, "trigger", "OnValidate", null, null, null, 2),
            new(3, "local procedure", "CheckCredit", null, null, null, 3),
            new(4, "event publisher", "OnAfterPost", null, null, null, 4),
        };
        var outline = new ObjectOutline(1, "table", 37, "CRONUS Line", 1, "CRONUS", null, null, 1, symbols);

        var result = ObjectOutlineResult.From(outline, memberKind: "procedure, trigger", namePattern: null, cap: 150);

        result.Outline.Symbols.Select(sym => sym.Name).Should().Equal("OnValidate", "CheckCredit");
        result.TotalSymbols.Should().Be(2);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Capped_rows_say_when_more_matched_and_how_to_narrow()
    {
        var fits = CappedRows<int>.From([1, 2, 3], cap: 3, "objects", "best matches first", "Narrow it.");
        fits.Truncated.Should().BeFalse();
        fits.Note.Should().BeNull();

        var cut = CappedRows<int>.From([1, 2, 3, 4], cap: 3, "objects", "best matches first", "Narrow it.");
        cut.Rows.Should().Equal(1, 2, 3);
        cut.Truncated.Should().BeTrue();
        cut.Note.Should().Be("Showing the first 3 objects (best matches first); more were left out. Narrow it.");
    }

    [Fact]
    public void Capped_rows_stop_at_the_character_budget_when_the_rows_are_long()
    {
        // 30 rows of 1,000 characters each is past the budget long before the cap of 50 (#1524).
        var rows = Enumerable.Range(1, 30).Select(i => new string('x', 1_000)).ToList();

        var cut = CappedRows<string>.From(rows, cap: 50, "lines", "in line order", "Narrow it.");

        cut.Rows.Count.Should().BeLessThan(30).And.BeGreaterThan(0);
        cut.Rows.Sum(r => r.Length + 3).Should().BeLessThanOrEqualTo(McpAnswerBudget.Characters);
        cut.Truncated.Should().BeTrue();
        cut.Note.Should().Be($"Showing the first {cut.Rows.Count} lines (in line order); the rest would make the answer too long to read in one go. Narrow it.");
    }

    [Fact]
    public void The_character_budget_always_keeps_one_row()
    {
        McpAnswerBudget.Fit([new string('x', 50_000), "short"], cap: 10).Should().Be(1,
            "an answer that matched something never comes back empty");
        McpAnswerBudget.Fit(Array.Empty<string>(), cap: 10).Should().Be(0);
    }

    [Fact]
    public void An_outline_of_long_signatures_stops_at_the_character_budget()
    {
        var signature = "procedure OnBeforePostSalesDoc(var SalesHeader: Record \"Sales Header\"; CommitIsSuppressed: Boolean; PreviewMode: Boolean; var HideProgressWindow: Boolean; var IsHandled: Boolean; var CalledBy: Integer)";
        var symbols = Enumerable.Range(1, 150)
            .Select(i => new ObjectSymbolRow(i, "event_publisher", $"OnBeforePost{i}", signature, null, null, i))
            .ToList();
        var outline = new ObjectOutline(1, "codeunit", 80, "Sales-Post", 1, "Microsoft", null, null, 1, symbols);

        var result = ObjectOutlineResult.From(outline, memberKind: null, namePattern: null, cap: 150);

        result.Outline.Symbols.Count.Should().BeLessThan(150);
        result.TotalSymbols.Should().Be(150);
        result.Truncated.Should().BeTrue();
        result.Note.Should().Contain($"first {result.Outline.Symbols.Count} of 150");
    }

    [Fact]
    public void An_outline_member_kind_may_be_written_with_a_space()
    {
        var symbols = new List<ObjectSymbolRow>
        {
            new(1, "local_procedure", "Helper", null, null, null, 1),
            new(2, "procedure", "Run", null, null, null, 2),
        };
        var outline = new ObjectOutline(1, "codeunit", 80, "CRONUS Post", 1, "CRONUS", null, null, 1, symbols);

        ObjectOutlineResult.From(outline, memberKind: "local procedure", namePattern: null, cap: 10)
            .Outline.Symbols.Select(sym => sym.Name).Should().Equal("Helper");
    }

    [Fact]
    public async Task GetObjectOutline_throws_McpException_for_unknown_object()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        await FluentActions.Awaiting(() => tools.GetObjectOutlineAsync(
                releaseId.ToString(), "Does Not Exist", "codeunit"))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("search_objects"));
    }

    [Fact]
    public async Task GetProcedureSource_returns_body_slice_by_symbolId()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Pick a body-bearing symbol from any codeunit. We resolve it
        // via the database directly to avoid coupling the test to a
        // specific DK Core procedure name.
        var symbolId = await ctx.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId)
            .Where(s => s.Kind == "procedure" || s.Kind == "trigger"
                     || s.Kind == "internal_procedure" || s.Kind == "local_procedure")
            .Where(s => s.EndLine != null)
            .Select(s => s.Id)
            .FirstOrDefaultAsync();
        symbolId.Should().NotBe(0, because: "the importer should have stamped EndLine on at least one procedure");

        var source = await tools.GetProcedureSourceAsync(releaseId.ToString(), symbolId: symbolId);
        source.SymbolId.Should().Be(symbolId);
        source.Source.Should().NotBeNullOrEmpty();
        source.EndLine.Should().BeGreaterThanOrEqualTo(source.StartLine);
    }

    [Fact]
    public async Task GetProcedureSource_throws_McpException_when_neither_symbolId_nor_name_supplied()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        await FluentActions.Awaiting(() => tools.GetProcedureSourceAsync(releaseId.ToString()))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("symbolId"));
    }

    [Fact]
    public async Task ListProcedureCalls_returns_outgoing_references_for_a_procedure()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Find a procedure that has at least one outgoing reference
        // stamped (source_symbol_id set). Most procedures in DK Core
        // emit a method_call / field_access into the Base App.
        var symbolId = await ctx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId)
            .Where(r => r.SourceSymbolId != null)
            .Select(r => r.SourceSymbolId!.Value)
            .FirstOrDefaultAsync();
        symbolId.Should().NotBe(0,
            because: "the importer should have stamped source_symbol_id on at least one method_call / field_access row");

        var calls = (await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId)).Rows;
        calls.Should().NotBeEmpty();
        calls.Should().AllSatisfy(c => c.TargetObjectName.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task ListProcedureCalls_falls_back_to_line_range_when_source_symbol_id_is_null_on_legacy_rows()
    {
        // Simulates a release imported before #181 landed: end_line is
        // set on the symbol (so the bound is known), but every
        // reference row from that body has source_symbol_id NULL.
        // The tool should fall back to the (source_object, line range)
        // scan and still return the calls.
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        var symbolId = await ctx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId && r.SourceSymbolId != null)
            .Select(r => r.SourceSymbolId!.Value)
            .FirstAsync();

        // Strip source_symbol_id on every reference belonging to this
        // procedure to simulate the pre-#181 ingest. Bulk update via
        // ExecuteSqlInterpolated so we don't have to load + track each
        // row individually.
        var affected = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE oe_module_references SET source_symbol_id = NULL WHERE source_symbol_id = {symbolId}");
        affected.Should().BeGreaterThan(0,
            because: "this fixture procedure has stamped rows we just nulled");

        var calls = (await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId)).Rows;
        calls.Should().NotBeEmpty(
            because: "the line-range fallback should recover the same rows once the FK column is empty");
    }

    // ---- helpers -----------------------------------------------------------

    private async Task SeedDefaultTemplateAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-test"));
        await ctx.SaveChangesAsync();
    }

    private WorkspaceTools NewWorkspaceTools(
        Data.AppDbContext ctx, IOptions<McpOptions>? options = null, GeneratedDownloadStore? downloads = null)
    {
        var templates = new TemplateService(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx));
        var modules = new ModuleService(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx));
        var catalog = new CatalogService(ctx, NullLogger<CatalogService>.Instance, _db.OrgContext);
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        var generation = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
        // The download link belongs to whoever generated it, so a generating test needs a user.
        _db.OrgContext.CurrentUserId ??= GeneratingUserId;
        return new WorkspaceTools(templates, modules, catalog, generation, delivery: null!, repositories: null!,
            downloads ?? _downloads, _db.OrgContext, options ?? Options);
    }

    private const int GeneratingUserId = 4242;

    private readonly GeneratedDownloadStore _downloads =
        new(TimeProvider.System);

    /// <summary>The bytes a generate result's link serves to the user who generated it.</summary>
    private byte[] Downloaded(WorkspaceResult result) =>
        _downloads.Get(result.DownloadPath![GeneratedDownloadStore.PathPrefix.Length..],
            _db.OrgContext.CurrentOrganizationId, _db.OrgContext.CurrentUserId)!.Content;
}
