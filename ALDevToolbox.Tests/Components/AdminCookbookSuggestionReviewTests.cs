using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Cookbook;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/admin/cookbook/suggestions/{id}</c>: a pending suggestion offers the
/// decision, a decided one says who decided it instead of reading as not found,
/// and a long keyword list is shortened on screen only.
/// </summary>
public sealed class AdminCookbookSuggestionReviewTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminCookbookSuggestionReviewTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        // The file browser's code viewer imports its CodeMirror module.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<RecipeService>();
        _ctx.Services.AddScoped<RecipeSuggestionService>();
        _ctx.Services.AddSingleton<MarkdownRenderer>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedAsync(
        RecipeSuggestionDecision decision = RecipeSuggestionDecision.Pending,
        string keywords = "alpha,beta",
        string? note = null)
    {
        await using var ctx = _db.NewContext();
        var reviewer = new User
        {
            Id = 801,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "reviewer@example.com",
            PasswordHash = "x",
            DisplayName = "Rita Reviewer",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(reviewer);
        Recipe? recipe = null;
        if (decision == RecipeSuggestionDecision.Approved)
        {
            recipe = RecipeBuilder.Default("Release rules");
            ctx.Recipes.Add(recipe);
        }
        var suggestion = new RecipeSuggestion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Title = "Release rules",
            Description = "First line.\nSecond line.",
            Keywords = keywords,
            Type = RecipeType.Pattern,
            Decision = decision,
            RequestedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            DecidedAt = decision == RecipeSuggestionDecision.Pending
                ? null
                : new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            DecidedByUserId = decision == RecipeSuggestionDecision.Pending ? null : reviewer.Id,
            DecisionNote = note,
            ApprovedRecipe = recipe,
            Files =
            {
                new RecipeSuggestionFile
                {
                    OrganizationId = TestDb.DefaultOrgId, Ordering = 0,
                    RelativePath = "src/Codeunits", FileName = "Check.Codeunit.al", Content = "codeunit 50100 Check {}",
                },
                new RecipeSuggestionFile
                {
                    OrganizationId = TestDb.DefaultOrgId, Ordering = 1,
                    RelativePath = "src/Tables", FileName = "Rule.Table.al", Content = "table 50100 Rule {}",
                },
            },
        };
        ctx.RecipeSuggestions.Add(suggestion);
        await ctx.SaveChangesAsync();
        return suggestion.Id;
    }

    [Fact]
    public async Task Pending_suggestion_offers_approve_and_reject_and_lists_its_files_in_a_tree()
    {
        var id = await SeedAsync();

        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Approve and publish");
            cut.Markup.Should().Contain("Reject");
            var rows = cut.FindAll(".fb__tree .otree__row").Select(r => r.TextContent.Trim()).ToList();
            rows.Should().Equal("src", "Codeunits", "Check.Codeunit.al", "Tables", "Rule.Table.al");
            cut.Find(".fb__path").TextContent.Should().Be("src/Codeunits/Check.Codeunit.al",
                "the first file opens by default");
        });
    }

    [Fact]
    public async Task Approved_suggestion_says_who_approved_it_and_offers_no_decision()
    {
        var id = await SeedAsync(RecipeSuggestionDecision.Approved);

        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Approved by Rita Reviewer");
            cut.Markup.Should().NotContain("Suggestion not found");
            cut.Markup.Should().NotContain("Approve and publish",
                "a decided suggestion can't be decided again");
            cut.Find(".page-head__actions a").GetAttribute("href")
                .Should().StartWith("/cookbook/", "the way on is the published recipe");
        });
    }

    [Fact]
    public async Task Rejected_suggestion_shows_the_reason()
    {
        var id = await SeedAsync(RecipeSuggestionDecision.Rejected, note: "Duplicate of an existing recipe");

        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Rejected by Rita Reviewer");
            cut.Markup.Should().Contain("Duplicate of an existing recipe");
        });
    }

    [Fact]
    public async Task Long_keyword_list_shows_ten_until_expanded()
    {
        var keywords = string.Join(',', Enumerable.Range(1, 14).Select(i => $"kw{i}"));
        var id = await SeedAsync(keywords: keywords);

        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() => cut.FindAll(".suggestion-tags .tag").Should().HaveCount(10));
        cut.Find(".suggestion-more").TextContent.Trim().Should().Be("and 4 more");

        cut.Find(".suggestion-more").Click();

        cut.WaitForAssertion(() => cut.FindAll(".suggestion-tags .tag").Should().HaveCount(14));
    }

    [Fact]
    public async Task Approve_asks_first_and_cancel_leaves_the_suggestion_pending()
    {
        var id = await SeedAsync();
        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => cut.Find(".page-head__actions .btn--primary"));

        cut.Find(".page-head__actions .btn--primary").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Publish this recipe?"));
        cut.FindAll(".confirm-dialog button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Publish this recipe?"));
        await using var read = _db.NewContext();
        (await read.RecipeSuggestions.SingleAsync(s => s.Id == id)).Decision
            .Should().Be(RecipeSuggestionDecision.Pending);
    }

    [Fact]
    public async Task Clicking_a_file_in_the_tree_shows_that_file()
    {
        var id = await SeedAsync();
        var cut = _ctx.Render<AdminCookbookSuggestionReview>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => cut.Find(".fb__tree"));

        cut.FindAll(".fb__tree .otree__row").Single(r => r.TextContent.Trim() == "Rule.Table.al").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fb__path").TextContent.Should().Be("src/Tables/Rule.Table.al");
            cut.Find(".fb__tree .otree__row.is-active").TextContent.Trim().Should().Be("Rule.Table.al");
        });
    }
}
