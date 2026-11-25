using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Validation coverage for <see cref="RecipeFile.RelativePath"/>: dot-dot
/// segments, leading slashes, and other path-injection shapes are refused
/// with field-keyed errors. Empty path stays at the root.
/// </summary>
public sealed class RecipeRelativePathTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/../bar")]
    [InlineData("./hidden")]
    [InlineData("seg/with/?question")]
    public async Task Relative_path_validation_rejects_invalid_shapes(string path)
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        Func<Task> act = () => svc.CreateAsync(new RecipeInput(
            "Bad path " + path, "Body.", "", RecipeType.Pattern, false,
            new[] { new RecipeFileInput("A.al", "// a", path) }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Files[0].RelativePath");
    }

    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("Sales/Posting")]
    [InlineData("Folder With Spaces/Sub_Folder")]
    public async Task Relative_path_validation_accepts_valid_shapes(string path)
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var recipe = await svc.CreateAsync(new RecipeInput(
            "Good path " + path, "Body.", "", RecipeType.Pattern, false,
            new[] { new RecipeFileInput("A.al", "// a", path) }));

        await using var verify = _db.NewContext();
        var persisted = await verify.Recipes
            .Include(s => s.Files)
            .SingleAsync(s => s.Id == recipe.Id);
        persisted.Files.Should().ContainSingle()
            .Which.RelativePath.Should().Be(path);
    }

    [Fact]
    public async Task Relative_path_normalises_leading_and_trailing_slashes()
    {
        // Authors sometimes paste a leading or trailing slash; the service
        // normalises it away before validation fires.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var recipe = await svc.CreateAsync(new RecipeInput(
            "Normalised", "Body.", "", RecipeType.Pattern, false,
            new[] { new RecipeFileInput("A.al", "// a", "/Sales/Posting/") }));

        await using var verify = _db.NewContext();
        (await verify.Recipes.Include(s => s.Files).SingleAsync(s => s.Id == recipe.Id))
            .Files.Single().RelativePath.Should().Be("Sales/Posting");
    }

    [Fact]
    public async Task Relative_path_normalises_backslashes_to_forward()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var recipe = await svc.CreateAsync(new RecipeInput(
            "Backslash", "Body.", "", RecipeType.Pattern, false,
            new[] { new RecipeFileInput("A.al", "// a", "Sales\\Posting") }));

        await using var verify = _db.NewContext();
        (await verify.Recipes.Include(s => s.Files).SingleAsync(s => s.Id == recipe.Id))
            .Files.Single().RelativePath.Should().Be("Sales/Posting");
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx), new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));
}
