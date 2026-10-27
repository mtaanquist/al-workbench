using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.Mcp.Tools;
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

        var rows = await tools.SearchAsync(query: "posting");

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
            new RecipeService(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx)),
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
        var tools = new WorkspaceTools(templates, modules, catalog, generation: null!, delivery: null!, repositories: null!, Options);

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
    public async Task GenerateWorkspace_returns_inline_base64_zip_with_sha256()
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
        result.ContentBase64.Should().NotBeNullOrEmpty();
        result.SizeBytes.Should().BeGreaterThan(0);
        result.Sha256.Should().HaveLength(64);

        // Round-trip: base64 → bytes → ZipArchive → recognisable entry.
        var bytes = Convert.FromBase64String(result.ContentBase64);
        bytes.Length.Should().Be(result.SizeBytes);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName.EndsWith(".code-workspace"));
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
        using (var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(leftOut.ContentBase64)), ZipArchiveMode.Read))
        {
            zip.Entries.Should().Contain(e => e.FullName == "CRONUSAS/Banking/src/Bank.Codeunit.al");
            zip.Entries.Should().NotContain(e => e.FullName.Contains("/Retired/"));
        }

        var none = await tools.GenerateWorkspaceAsync(Input(Array.Empty<string>()));
        using (var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(none.ContentBase64)), ZipArchiveMode.Read))
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
            .Where(ex => ex.Message.Contains("MaxWorkspaceBytes"));
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
        all.Rows.Should().HaveCount(200);
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

        var labels = (await NewOeTools(ctx).ListReleasesAsync()).Select(r => r.Label).ToList();

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
        var found = await tools.SearchObjectsAsync(releaseId.ToString(), namePattern: "", kind: "codeunit");
        found.Should().NotBeEmpty(because: "DK Core ships at least one codeunit");
        var target = found.First();

        var outline = await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit");
        outline.Name.Should().Be(target.Name);
        outline.Symbols.Should().NotBeEmpty(
            because: "every imported codeunit has at least one procedure / trigger row");
        outline.Symbols.Should().AllSatisfy(s => s.Id.Should().BeGreaterThan(0));
        outline.Symbols.Should().BeInAscendingOrder(s => s.LineNumber);
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

        var calls = await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId);
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

        var calls = await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId);
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

    private WorkspaceTools NewWorkspaceTools(Data.AppDbContext ctx, IOptions<McpOptions>? options = null)
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
        return new WorkspaceTools(templates, modules, catalog, generation, delivery: null!, repositories: null!, options ?? Options);
    }
}
