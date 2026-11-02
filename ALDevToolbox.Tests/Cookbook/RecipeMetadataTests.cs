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
/// Coverage for the Instructions + MinimumApplicationVersion + Type fields on
/// <see cref="Recipe"/>: round-trip on create/update, validation of
/// missing / soft-deleted application versions, and that
/// <see cref="RecipeService.GetAsync"/> eagerly loads the navigation entity
/// so the badge can render without an extra round-trip.
/// </summary>
public sealed class RecipeMetadataTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<ApplicationVersion> SeedApplicationVersionAsync(
        string key = "bc28",
        string name = "BC 2026 Wave 1",
        string app = "28.0.0.0",
        string runtime = "28.0",
        bool deprecated = false,
        bool deleted = false)
    {
        await using var ctx = _db.NewContext();
        var row = new ApplicationVersion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Key = key,
            Name = name,
            Application = app,
            Runtime = runtime,
            Ordering = 0,
            Deprecated = deprecated,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        ctx.ApplicationVersions.Add(row);
        await ctx.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Create_persists_instructions_and_minimum_application_version()
    {
        var version = await SeedApplicationVersionAsync();

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var recipe = await svc.CreateAsync(new RecipeInput(
            Title: "With metadata",
            Description: "Carries the new fields.",
            Keywords: "meta",
            Type: RecipeType.Snippet,
            Deprecated: false,
            Files: new[] { new RecipeFileInput("A.al", "// a") },
            Instructions: "  ## Setup\n\nDrop into `src/`.  ",
            MinimumApplicationVersionId: version.Id));

        await using var verify = _db.NewContext();
        var persisted = await verify.Recipes
            .Include(s => s.MinimumApplicationVersion)
            .SingleAsync(s => s.Id == recipe.Id);
        persisted.Instructions.Should().Be("## Setup\n\nDrop into `src/`.",
            "instructions are trimmed but otherwise preserved verbatim");
        persisted.MinimumApplicationVersionId.Should().Be(version.Id);
        persisted.MinimumApplicationVersion!.Application.Should().Be("28.0.0.0");
    }

    [Fact]
    public async Task Create_treats_whitespace_instructions_as_null()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var recipe = await svc.CreateAsync(new RecipeInput(
            "Empty instructions", "Body.", "", RecipeType.Snippet, false,
            new[] { new RecipeFileInput("A.al", "// a") },
            Instructions: "   \n  ",
            MinimumApplicationVersionId: null));

        await using var verify = _db.NewContext();
        (await verify.Recipes.SingleAsync(s => s.Id == recipe.Id))
            .Instructions.Should().BeNull();
    }

    [Fact]
    public async Task Update_round_trips_metadata_and_clears_it_back_to_null()
    {
        var version = await SeedApplicationVersionAsync();
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Round Trip").WithFile("A.al", "// a");
            s.Instructions = "first version";
            s.MinimumApplicationVersionId = version.Id;
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        // Clear both fields back to null.
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(recipeId, new RecipeInput(
                "Round Trip", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                Instructions: null,
                MinimumApplicationVersionId: null));
        }

        await using var verify = _db.NewContext();
        var row = await verify.Recipes.SingleAsync(s => s.Id == recipeId);
        row.Instructions.Should().BeNull();
        row.MinimumApplicationVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Create_rejects_oversized_instructions_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var oversized = new string('x', RecipeService.MaxInstructionsLength + 1);

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new RecipeInput(
                "Too long", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                Instructions: oversized)));

        ex.Errors.Should().ContainKey("Instructions");
    }

    [Fact]
    public async Task Create_rejects_unknown_minimum_application_version_id()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new RecipeInput(
                "Bad FK", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                MinimumApplicationVersionId: 9999)));

        ex.Errors.Should().ContainKey("MinimumApplicationVersionId");
    }

    [Fact]
    public async Task Create_rejects_soft_deleted_minimum_application_version()
    {
        var deleted = await SeedApplicationVersionAsync(deleted: true);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new RecipeInput(
                "Soft-deleted FK", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                MinimumApplicationVersionId: deleted.Id)));

        ex.Errors.Should().ContainKey("MinimumApplicationVersionId");
    }

    [Fact]
    public async Task Create_accepts_deprecated_minimum_application_version()
    {
        // Deprecated rows must stay assignable so an existing recipe doesn't
        // break when admins clean up the catalogue.
        var deprecated = await SeedApplicationVersionAsync(deprecated: true);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var recipe = await svc.CreateAsync(new RecipeInput(
            "Deprecated FK is fine", "Body.", "", RecipeType.Snippet, false,
            new[] { new RecipeFileInput("A.al", "// a") },
            MinimumApplicationVersionId: deprecated.Id));

        recipe.MinimumApplicationVersionId.Should().Be(deprecated.Id);
    }

    [Fact]
    public async Task Get_eagerly_loads_minimum_application_version()
    {
        var version = await SeedApplicationVersionAsync();
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Eager").WithFile("A.al", "// a");
            s.MinimumApplicationVersionId = version.Id;
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var loaded = await NewService(ctx2).GetAsync(recipeId);
        loaded.Should().NotBeNull();
        loaded!.MinimumApplicationVersion.Should().NotBeNull(
            "GetAsync includes the navigation so the badge can render without N+1");
        loaded.MinimumApplicationVersion!.Name.Should().Be("BC 2026 Wave 1");
    }

    [Fact]
    public async Task Type_round_trips_through_create_and_update()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync(new RecipeInput(
            "Type Test", "Body.", "", RecipeType.Module, false,
            new[] { new RecipeFileInput("A.al", "// a") }));

        await using (var verify = _db.NewContext())
        {
            (await verify.Recipes.SingleAsync(s => s.Id == created.Id))
                .Type.Should().Be(RecipeType.Module);
        }

        await using (var update = _db.NewContext())
        {
            await NewService(update).UpdateAsync(created.Id, new RecipeInput(
                "Type Test", "Body.", "", RecipeType.Pattern, false,
                new[] { new RecipeFileInput("A.al", "// a") }));
        }

        await using var verifyUpdate = _db.NewContext();
        (await verifyUpdate.Recipes.SingleAsync(s => s.Id == created.Id))
            .Type.Should().Be(RecipeType.Pattern);
    }

    [Fact]
    public async Task EstimatedValueHours_round_trips_and_clears_back_to_null()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var created = await NewService(ctx).CreateAsync(new RecipeInput(
                "Valued", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                EstimatedValueHours: 8.5m));
            recipeId = created.Id;
        }

        await using (var verify = _db.NewContext())
        {
            (await verify.Recipes.SingleAsync(s => s.Id == recipeId))
                .EstimatedValueHours.Should().Be(8.5m);
        }

        await using (var update = _db.NewContext())
        {
            await NewService(update).UpdateAsync(recipeId, new RecipeInput(
                "Valued", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                EstimatedValueHours: null));
        }

        await using var verifyUpdate = _db.NewContext();
        (await verifyUpdate.Recipes.SingleAsync(s => s.Id == recipeId))
            .EstimatedValueHours.Should().BeNull();
    }

    [Fact]
    public async Task Create_rejects_negative_estimated_value_hours()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).CreateAsync(new RecipeInput(
                "Negative", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                EstimatedValueHours: -1m)));

        ex.Errors.Should().ContainKey("EstimatedValueHours");
    }

    [Fact]
    public async Task Create_rejects_estimated_value_hours_over_cap()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).CreateAsync(new RecipeInput(
                "Too much", "Body.", "", RecipeType.Snippet, false,
                new[] { new RecipeFileInput("A.al", "// a") },
                EstimatedValueHours: RecipeService.MaxEstimatedValueHours + 1m)));

        ex.Errors.Should().ContainKey("EstimatedValueHours");
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx), new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));
}
