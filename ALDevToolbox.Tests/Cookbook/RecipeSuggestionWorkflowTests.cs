using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Approval workflow for <see cref="RecipeSuggestionService"/>: submit creates
/// a pending row, approve promotes it to a real <see cref="Recipe"/>
/// atomically with the decision columns, reject closes it with an optional
/// note. The title-clash guard on approval prevents a duplicate recipe
/// landing if another suggestion was approved with the same title in between.
/// </summary>
public sealed class RecipeSuggestionWorkflowTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedUserAsync(int userId = 700, string display = "Submitter", UserRole role = UserRole.User)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{userId}@example.com",
            PasswordHash = "x",
            DisplayName = display,
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Submit_creates_pending_suggestion_with_files()
    {
        var user = await SeedUserAsync();
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        var svc = NewSuggestionService(ctx);
        var id = await svc.SubmitAsync(new RecipeSuggestionInput(
            Title: "Idea",
            Description: "A useful pattern.",
            Keywords: "alpha beta",
            Type: RecipeType.Pattern,
            Files: new[]
            {
                new RecipeFileInput("Sub.Codeunit.al", "// sub"),
                new RecipeFileInput("Ext.PageExt.al", "// ext"),
            }));

        await using var read = _db.NewContext();
        var row = await read.RecipeSuggestions
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == id);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Type.Should().Be(RecipeType.Pattern);
        row.SuggestedByUserId.Should().Be(user.Id);
        row.DecidedAt.Should().BeNull();
        row.ApprovedRecipeId.Should().BeNull();
        row.Files.Select(f => f.FileName).Should().Equal("Sub.Codeunit.al", "Ext.PageExt.al");
    }

    [Fact]
    public async Task Submit_rejects_when_no_files_supplied()
    {
        var user = await SeedUserAsync(userId: 701);
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        var svc = NewSuggestionService(ctx);
        Func<Task> act = () => svc.SubmitAsync(new RecipeSuggestionInput(
            "Idea", "Body.", "", RecipeType.Snippet, Array.Empty<RecipeFileInput>()));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Files");
    }

    [Fact]
    public async Task Approve_promotes_suggestion_to_recipe_and_stamps_decision()
    {
        var user = await SeedUserAsync(userId: 702);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Approve Me",
                Description: "Pending body.",
                Keywords: "approve",
                Type: RecipeType.Pattern,
                Files: new[] { new RecipeFileInput("Sample.al", "// content", "src") }));
        }

        var admin = await SeedUserAsync(userId: 703, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        Recipe recipe;
        await using (var ctx = _db.NewContext())
        {
            recipe = await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var verify = _db.NewContext();
        var persistedRecipe = await verify.Recipes
            .Include(s => s.Files)
            .SingleAsync(s => s.Id == recipe.Id);
        persistedRecipe.Title.Should().Be("Approve Me");
        persistedRecipe.Type.Should().Be(RecipeType.Pattern,
            "Type is carried through from the suggestion on approval");
        persistedRecipe.Files.Should().ContainSingle(f =>
            f.FileName == "Sample.al" && f.Content == "// content" && f.RelativePath == "src");

        var persistedSuggestion = await verify.RecipeSuggestions.SingleAsync(s => s.Id == suggestionId);
        persistedSuggestion.Decision.Should().Be(RecipeSuggestionDecision.Approved);
        persistedSuggestion.ApprovedRecipeId.Should().Be(recipe.Id);
        persistedSuggestion.DecidedByUserId.Should().Be(admin.Id);
        persistedSuggestion.DecidedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Approve_refuses_when_title_already_taken_by_existing_recipe()
    {
        var user = await SeedUserAsync(userId: 704);
        _db.OrgContext.CurrentUserId = user.Id;

        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Taken").WithFile("A.al", "// existing"));
            await ctx.SaveChangesAsync();
        }

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Taken",
                Description: "Same name, different body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("B.al", "// new") }));
        }

        var admin = await SeedUserAsync(userId: 705, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).ApproveAsync(suggestionId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");

        // Suggestion remains pending — no recipe was created from it.
        await using var verify = _db.NewContext();
        (await verify.RecipeSuggestions.SingleAsync(s => s.Id == suggestionId))
            .Decision.Should().Be(RecipeSuggestionDecision.Pending);
    }

    [Fact]
    public async Task Approve_succeeds_when_title_only_matches_a_soft_deleted_recipe()
    {
        var user = await SeedUserAsync(userId: 720);
        _db.OrgContext.CurrentUserId = user.Id;

        await using (var ctx = _db.NewContext())
        {
            var deleted = RecipeBuilder.Default("Reborn").WithFile("Old.al", "// old");
            deleted.DeletedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            ctx.Recipes.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                "Reborn", "Fresh take.", "", RecipeType.Snippet,
                new[] { new RecipeFileInput("New.al", "// new") }));
        }

        var admin = await SeedUserAsync(userId: 721, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using (var ctx = _db.NewContext())
        {
            var recipe = await NewSuggestionService(ctx).ApproveAsync(suggestionId);
            recipe.Title.Should().Be("Reborn");
        }

        await using var verify = _db.NewContext();
        (await verify.Recipes.CountAsync(s => s.Title == "Reborn")).Should().Be(2);
        (await verify.RecipeSuggestions.SingleAsync(s => s.Id == suggestionId))
            .Decision.Should().Be(RecipeSuggestionDecision.Approved);
    }

    [Fact]
    public async Task Submit_and_approve_carry_estimated_value_hours_to_recipe()
    {
        var user = await SeedUserAsync(userId: 730);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                "Valued", "Saves real time.", "", RecipeType.Snippet,
                new[] { new RecipeFileInput("A.al", "// a") },
                EstimatedValueHours: 7.5m));
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.RecipeSuggestions.SingleAsync(s => s.Id == suggestionId))
                .EstimatedValueHours.Should().Be(7.5m);
        }

        var admin = await SeedUserAsync(userId: 731, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            recipeId = (await NewSuggestionService(ctx).ApproveAsync(suggestionId)).Id;
        }

        await using var read = _db.NewContext();
        (await read.Recipes.SingleAsync(r => r.Id == recipeId))
            .EstimatedValueHours.Should().Be(7.5m);
    }

    [Fact]
    public async Task Submit_rejects_negative_estimated_value_hours()
    {
        var user = await SeedUserAsync(userId: 732);
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
            "Negative", "Body.", "", RecipeType.Snippet,
            new[] { new RecipeFileInput("A.al", "// a") },
            EstimatedValueHours: -1m));
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EstimatedValueHours");
    }

    [Fact]
    public async Task Reject_records_decision_and_optional_note()
    {
        var user = await SeedUserAsync(userId: 706);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                "Drop Me", "Body.", "", RecipeType.Snippet, new[] { new RecipeFileInput("A.al", "// a") }));
        }

        var admin = await SeedUserAsync(userId: 707, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).RejectAsync(suggestionId, " Too narrow. ");
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions.SingleAsync(s => s.Id == suggestionId);
        row.Decision.Should().Be(RecipeSuggestionDecision.Rejected);
        row.DecidedByUserId.Should().Be(admin.Id);
        row.DecisionNote.Should().Be("Too narrow.");
        row.ApprovedRecipeId.Should().BeNull();

        (await verify.Recipes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Approve_carries_instructions_and_minimum_application_version_to_recipe()
    {
        ApplicationVersion version;
        await using (var ctx = _db.NewContext())
        {
            version = new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc28",
                Name = "BC 2026 Wave 1",
                Application = "28.0.0.0",
                Runtime = "28.0",
                Ordering = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.ApplicationVersions.Add(version);
            await ctx.SaveChangesAsync();
        }

        var user = await SeedUserAsync(userId: 720);
        _db.OrgContext.CurrentUserId = user.Id;
        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Carry metadata",
                Description: "Body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("A.al", "// a") },
                Instructions: "## Setup\n\nDrop into `src/`.",
                MinimumApplicationVersionId: version.Id));
        }

        var admin = await SeedUserAsync(userId: 721, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;
        Recipe recipe;
        await using (var ctx = _db.NewContext())
        {
            recipe = await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var verify = _db.NewContext();
        var promoted = await verify.Recipes
            .Include(s => s.MinimumApplicationVersion)
            .SingleAsync(s => s.Id == recipe.Id);
        promoted.Instructions.Should().Be("## Setup\n\nDrop into `src/`.");
        promoted.MinimumApplicationVersionId.Should().Be(version.Id);
        promoted.MinimumApplicationVersion!.Name.Should().Be("BC 2026 Wave 1");
    }

    /// <summary>
    /// #1278 (T26): rejecting a suggestion that has already been decided -
    /// approved or rejected - is refused, and the first decision stands.
    /// </summary>
    [Theory]
    [InlineData(true, "approved")]
    [InlineData(false, "rejected")]
    public async Task Reject_refuses_when_already_decided(bool approveFirst, string decided)
    {
        var user = await SeedUserAsync(userId: 720);
        _db.OrgContext.CurrentUserId = user.Id;
        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                "Decided once", "Body.", "", RecipeType.Snippet, new[] { new RecipeFileInput("A.al", "// a") }));
        }

        var admin = await SeedUserAsync(userId: 721, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;
        await using (var ctx = _db.NewContext())
        {
            if (approveFirst) await NewSuggestionService(ctx).ApproveAsync(suggestionId);
            else await NewSuggestionService(ctx).RejectAsync(suggestionId, "first");
        }

        await using (var ctx = _db.NewContext())
        {
            Func<Task> act = () => NewSuggestionService(ctx).RejectAsync(suggestionId, "second");
            var ex = await act.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors.Should().ContainKey("Decision");
            ex.Which.Errors["Decision"].Should().Contain(decided);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions.AsNoTracking().SingleAsync(s => s.Id == suggestionId);
        row.Decision.Should().Be(approveFirst ? RecipeSuggestionDecision.Approved : RecipeSuggestionDecision.Rejected);
        row.DecisionNote.Should().Be(approveFirst ? null : "first");
    }

    [Fact]
    public async Task Approve_refuses_when_already_decided()
    {
        var user = await SeedUserAsync(userId: 708);
        _db.OrgContext.CurrentUserId = user.Id;
        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                "Once", "Body.", "", RecipeType.Snippet, new[] { new RecipeFileInput("A.al", "// a") }));
        }

        var admin = await SeedUserAsync(userId: 709, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).ApproveAsync(suggestionId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    [Fact]
    public async Task Update_replaces_fields_and_reconciles_files_for_pending_suggestion()
    {
        var user = await SeedUserAsync(userId: 720);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Original Title",
                Description: "Original body.",
                Keywords: "alpha",
                Type: RecipeType.Snippet,
                Files: new[]
                {
                    new RecipeFileInput("Keep.al", "// original keep"),
                    new RecipeFileInput("Drop.al", "// goes away"),
                }));
        }

        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).UpdateAsync(suggestionId, new RecipeSuggestionInput(
                Title: "Revised Title",
                Description: "Revised body.",
                Keywords: "alpha beta",
                Type: RecipeType.Pattern,
                Files: new[]
                {
                    new RecipeFileInput("Keep.al", "// revised keep"),
                    new RecipeFileInput("New.al", "// brand new"),
                }));
        }

        await using var read = _db.NewContext();
        var row = await read.RecipeSuggestions
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == suggestionId);
        row.Title.Should().Be("Revised Title");
        row.Description.Should().Be("Revised body.");
        row.Keywords.Should().Be("alpha,beta", "keywords normalise to a comma-separated tag list");
        row.Type.Should().Be(RecipeType.Pattern);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Files.Select(f => f.FileName).Should().Equal("Keep.al", "New.al");
        row.Files[0].Content.Should().Be("// revised keep");
        row.Files[1].Content.Should().Be("// brand new");
    }

    [Fact]
    public async Task Update_rejects_when_caller_is_not_the_submitter()
    {
        var owner = await SeedUserAsync(userId: 721);
        _db.OrgContext.CurrentUserId = owner.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Owned By 721",
                Description: "Body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("a.al", "// hi") }));
        }

        var other = await SeedUserAsync(userId: 722, display: "Someone Else");
        _db.OrgContext.CurrentUserId = other.Id;

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).UpdateAsync(suggestionId, new RecipeSuggestionInput(
            Title: "Hijacked",
            Description: "Body.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Files: new[] { new RecipeFileInput("a.al", "// hi") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SuggestedByUserId");
    }

    [Fact]
    public async Task Update_rejects_when_suggestion_is_already_approved()
    {
        var user = await SeedUserAsync(userId: 723);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Will Be Approved",
                Description: "Body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("a.al", "// hi") }));
        }

        var admin = await SeedUserAsync(userId: 724, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        _db.OrgContext.CurrentUserId = user.Id;
        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).UpdateAsync(suggestionId, new RecipeSuggestionInput(
            Title: "Will Be Approved",
            Description: "Updated body.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Files: new[] { new RecipeFileInput("a.al", "// hi") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    [Fact]
    public async Task Update_rejects_when_suggestion_is_already_rejected()
    {
        var user = await SeedUserAsync(userId: 725);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Will Be Rejected",
                Description: "Body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("a.al", "// hi") }));
        }

        var admin = await SeedUserAsync(userId: 726, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).RejectAsync(suggestionId, note: "no thanks");
        }

        _db.OrgContext.CurrentUserId = user.Id;
        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).UpdateAsync(suggestionId, new RecipeSuggestionInput(
            Title: "Will Be Rejected",
            Description: "Updated body.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Files: new[] { new RecipeFileInput("a.al", "// hi") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    [Fact]
    public async Task Update_runs_field_validation()
    {
        var user = await SeedUserAsync(userId: 727);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new RecipeSuggestionInput(
                Title: "Original",
                Description: "Body.",
                Keywords: "",
                Type: RecipeType.Snippet,
                Files: new[] { new RecipeFileInput("a.al", "// hi") }));
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).UpdateAsync(suggestionId, new RecipeSuggestionInput(
            Title: "",
            Description: "Body.",
            Keywords: "",
            Type: RecipeType.Snippet,
            Files: new[] { new RecipeFileInput("a.al", "// hi") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task Update_returns_validation_error_when_id_unknown()
    {
        var user = await SeedUserAsync(userId: 728);
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx).UpdateAsync(9_999_999, new RecipeSuggestionInput(
            Title: "X",
            Description: "Y",
            Keywords: "",
            Type: RecipeType.Snippet,
            Files: new[] { new RecipeFileInput("a.al", "// hi") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Id");
    }

    private RecipeSuggestionService NewSuggestionService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeSuggestionService>.Instance, _db.OrgContext);
}
